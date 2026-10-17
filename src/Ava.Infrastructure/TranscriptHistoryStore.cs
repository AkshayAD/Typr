using System.Text.Json;

namespace Ava.Infrastructure;

public sealed record TranscriptEntry(Guid Id, DateTimeOffset CreatedAt, string Text, string Mode);

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
        }
        entries = Array.AsReadOnly(document.Entries.OrderByDescending(entry => entry.CreatedAt).ToArray());
        loaded = true;
    }

    public void Add(string text, string mode)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
            throw new ArgumentException($"Transcript text must contain between 1 and {MaximumTextLength:N0} characters.", nameof(text));
        if (!IsMode(mode)) throw new ArgumentException("Transcript mode must be Clean or Raw.", nameof(mode));
        EnsureLoaded();
        var entry = new TranscriptEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, text, mode);
        Save(new[] { entry }.Concat(entries).Take(Capacity).ToArray());
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

    void Save(TranscriptEntry[] next)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(output, new HistoryDocument(1, next), JsonOptions);
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
