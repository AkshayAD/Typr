using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typr.Infrastructure;

public sealed record TranscriptRevision(DateTimeOffset CreatedAt, string Text, string? Model, string Kind,
    string? Provider = null, string? Reasoning = null);
public sealed record TranscriptEntry(Guid Id, DateTimeOffset CreatedAt, string Text, string Mode)
{
    public string RawText { get; init; } = "";
    public string? AudioFileName { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? Reasoning { get; init; }
    public string DeliveryStatus { get; init; } = "Saved";
    public long DurationMs { get; init; }
    IReadOnlyList<TranscriptRevision> revisions = Array.Empty<TranscriptRevision>();
    public IReadOnlyList<TranscriptRevision> Revisions
    {
        get => revisions;
        init => revisions = value is null ? null! : value.Count == 0
            ? Array.Empty<TranscriptRevision>() : Array.AsReadOnly(value.ToArray());
    }
    [JsonIgnore] public string Preview => Text.Length > 130 ? Text[..130] + "…" : Text;
    [JsonIgnore] public string DisplayDate => CreatedAt.ToLocalTime().ToString("ddd d MMM · HH:mm");
    [JsonIgnore] public string Detail => $"{Mode} · {Model ?? "Local recognition"} · {DeliveryStatus}";
}

/// <summary>Bounded local history. Failed reads and writes never replace the in-memory snapshot.</summary>
public sealed class TranscriptHistoryStore(string filePath)
{
    const int Capacity = 200;
    const int MaximumTextLength = 100_000;
    const long MaximumFileLength = 128L * 1024 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    IReadOnlyList<TranscriptEntry> entries = Array.AsReadOnly(Array.Empty<TranscriptEntry>());
    bool loaded;

    public IReadOnlyList<TranscriptEntry> Entries => entries;

    public void Load()
    {
        // A failed reload also blocks later mutations from overwriting unreadable data.
        loaded = false;
        if (!File.Exists(filePath)) {
            entries = Array.AsReadOnly(Array.Empty<TranscriptEntry>());
            loaded = true;
            return;
        }
        using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumFileLength) throw new InvalidDataException("Transcript history is too large to load safely.");
        var document = JsonSerializer.Deserialize<HistoryDocument>(input, JsonOptions)
            ?? throw new InvalidDataException("Transcript history is empty or invalid.");
        if (document.Version != 1) throw new InvalidDataException("This transcript history version is not supported.");
        if (document.Entries is null || document.Entries.Length > Capacity)
            throw new InvalidDataException("Transcript history has an invalid number of entries.");
        var ids = new HashSet<Guid>();
        foreach (var entry in document.Entries) {
            if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id) || entry.CreatedAt == default ||
                string.IsNullOrWhiteSpace(entry.Text) || entry.Text.Length > MaximumTextLength || !IsMode(entry.Mode))
                throw new InvalidDataException("Transcript history contains an invalid entry.");
            ValidateDetails(entry);
        }
        entries = Array.AsReadOnly(document.Entries.OrderByDescending(entry => entry.CreatedAt).ToArray());
        loaded = true;
    }

    public void Add(string text, string mode, string rawText = "", string? audioFileName = null,
        string? provider = null, string? model = null, string? reasoning = null, string deliveryStatus = "Saved", long durationMs = 0)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
            throw new ArgumentException($"Transcript text must contain between 1 and {MaximumTextLength:N0} characters.", nameof(text));
        if (!IsMode(mode)) throw new ArgumentException("Transcript mode must be Clean or Raw.", nameof(mode));
        EnsureLoaded();
        var entry = new TranscriptEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, text, mode)
        {
            RawText = rawText, AudioFileName = audioFileName, Provider = provider, Model = model,
            Reasoning = reasoning, DeliveryStatus = deliveryStatus, DurationMs = durationMs
        };
        ValidateDetails(entry);
        Save(new[] { entry }.Concat(entries).Take(Capacity).ToArray());
    }

    public void Revise(Guid id, string text, string model, string kind, string? provider = null, string? reasoning = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength) throw new ArgumentException("A revision must contain a completed transcript.", nameof(text));
        EnsureLoaded();
        var previous = entries.SingleOrDefault(entry => entry.Id == id) ?? throw new KeyNotFoundException("That transcript is no longer in history.");
        var updated = previous with
        {
            Text = text, Model = model, Provider = provider, Reasoning = reasoning,
            Revisions = previous.Revisions.Append(new TranscriptRevision(DateTimeOffset.UtcNow, previous.Text, previous.Model, kind,
                previous.Provider, previous.Reasoning)).TakeLast(10).ToArray()
        };
        ValidateDetails(updated);
        Save(entries.Select(entry => entry.Id == id ? updated : entry).ToArray());
    }

    public void RestorePrevious(Guid id)
    {
        EnsureLoaded();
        var entry = entries.SingleOrDefault(entry => entry.Id == id) ?? throw new KeyNotFoundException("That transcript is no longer in history.");
        if (entry.Revisions.Count == 0) return;
        var last = entry.Revisions[^1];
        var restored = entry with { Text = last.Text, Model = last.Model, Provider = last.Provider, Reasoning = last.Reasoning,
            Revisions = entry.Revisions.Take(entry.Revisions.Count - 1).ToArray() };
        Save(entries.Select(value => value.Id == id ? restored : value).ToArray());
    }

    public void Remove(Guid id)
    {
        EnsureLoaded();
        if (entries.Any(entry => entry.Id == id)) Save(entries.Where(entry => entry.Id != id).ToArray());
    }

    public void Clear()
    {
        EnsureLoaded();
        Save(Array.Empty<TranscriptEntry>());
    }

    void EnsureLoaded() { if (!loaded) Load(); }
    static bool IsMode(string? mode) => mode is "Clean" or "Raw";
    static void ValidateDetails(TranscriptEntry entry)
    {
        if (entry.RawText is null || entry.RawText.Length > MaximumTextLength || entry.DurationMs < 0 ||
            entry.Revisions is null || entry.Revisions.Count > 10 ||
            entry.Revisions.Any(revision => revision is null || revision.CreatedAt == default || string.IsNullOrWhiteSpace(revision.Text) || revision.Text.Length > MaximumTextLength || revision.Model?.Length > 200 || revision.Kind is null || revision.Kind.Length > 100 || revision.Provider?.Length > 100 || revision.Reasoning?.Length > 50) ||
            entry.Provider?.Length > 100 || entry.Model?.Length > 200 || entry.Reasoning?.Length > 50 || entry.DeliveryStatus is null || entry.DeliveryStatus.Length > 200 ||
            entry.AudioFileName is { } audio && (audio.Length > 150 || Path.GetFileName(audio) != audio || !audio.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || audio.Contains("..") || audio.Contains(':') || audio.Contains('/') || audio.Contains('\\')))
            throw new InvalidDataException("Transcript history contains invalid recovery metadata.");
    }

    void Save(TranscriptEntry[] next)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(output, new HistoryDocument(1, next), JsonOptions);
                if (output.Length > MaximumFileLength)
                    throw new InvalidDataException("Transcript history has reached its storage limit. Remove older entries before saving more text or revisions.");
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
            entries = Array.AsReadOnly(next);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    sealed record HistoryDocument(int Version, TranscriptEntry[] Entries);
}
