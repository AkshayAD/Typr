using System.Security.Cryptography;
using System.Text.Json;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

public sealed class TranscriptRevisionTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "typr-revision-tests-" + Guid.NewGuid().ToString("N"));
    string HistoryPath => Path.Combine(directory, "history.json");

    [Fact] public void RevisionsRoundtripAndRestoreTextModelProviderAndReasoningTogether()
    {
        var store = new TranscriptHistoryStore(HistoryPath);
        store.Add("Local result", "Clean", "Raw source", "synthetic.wav", durationMs: 1200);
        var original = Assert.Single(store.Entries);
        store.Revise(original.Id, "ChatGPT result", "gpt-6-luna", "Repolish", "ChatGPT", "low");
        store.Revise(original.Id, "Claude result", "haiku", "Repolish", "Claude", "high");
        var current = Assert.Single(store.Entries);
        Assert.Equal("Claude", current.Provider);
        Assert.Equal("high", current.Reasoning);
        Assert.Equal(2, current.Revisions.Count);
        Assert.Equal("Local result", original.Text);
        Assert.Empty(original.Revisions);
        var reloaded = new TranscriptHistoryStore(HistoryPath);
        reloaded.Load();
        Assert.Equal(JsonSerializer.Serialize(current), JsonSerializer.Serialize(Assert.Single(reloaded.Entries)));
        reloaded.RestorePrevious(original.Id);
        var restored = Assert.Single(reloaded.Entries);
        Assert.Equal("ChatGPT result", restored.Text);
        Assert.Equal("gpt-6-luna", restored.Model);
        Assert.Equal("ChatGPT", restored.Provider);
        Assert.Equal("low", restored.Reasoning);
        Assert.Equal("Raw source", restored.RawText);
        Assert.Equal("synthetic.wav", restored.AudioFileName);
        Assert.Equal(1200, restored.DurationMs);
        reloaded.RestorePrevious(original.Id);
        restored = Assert.Single(reloaded.Entries);
        Assert.Equal("Local result", restored.Text);
        Assert.Null(restored.Model);
        Assert.Null(restored.Provider);
        Assert.Null(restored.Reasoning);
        Assert.Empty(restored.Revisions);
        store.Load();
        Assert.Equal(restored, Assert.Single(store.Entries));
    }

    [Fact] public void RevisionCollectionsAreDefensiveReadOnlySnapshotsAndRemainBounded()
    {
        var first = new TranscriptRevision(DateTimeOffset.UtcNow, "First", null, "Repolish");
        var source = new[] { first };
        var entry = new TranscriptEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "Current", "Clean") { Revisions = source };
        source[0] = first with { Text = "Mutated" };
        Assert.Equal("First", Assert.Single(entry.Revisions).Text);
        Assert.Throws<NotSupportedException>(() => ((IList<TranscriptRevision>)entry.Revisions)[0] = source[0]);
        var store = new TranscriptHistoryStore(HistoryPath);
        store.Add("Version 0", "Clean");
        var id = store.Entries[0].Id;
        for (int i = 1; i <= 12; i++) store.Revise(id, $"Version {i}", $"model-{i}", "Repolish", "ChatGPT", "low");
        Assert.Equal(10, store.Entries[0].Revisions.Count);
        Assert.Equal("Version 2", store.Entries[0].Revisions[0].Text);
        Assert.Throws<NotSupportedException>(() => ((IList<TranscriptRevision>)store.Entries[0].Revisions).Clear());
        var loaded = new TranscriptHistoryStore(HistoryPath);
        loaded.Load();
        Assert.Equal(JsonSerializer.Serialize(store.Entries), JsonSerializer.Serialize(loaded.Entries));
        Assert.Throws<NotSupportedException>(() => ((IList<TranscriptRevision>)loaded.Entries[0].Revisions).Clear());
    }

    [Fact] public void LegacyEntriesAndRevisionsLoadWithoutMetadataAndDisplayGettersAreNotPersisted()
    {
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(new { Version = 1, Entries = new[] {
            new { Id = id, CreatedAt = now, Text = "Legacy", Mode = "Clean", Revisions = new[] {
                new { CreatedAt = now, Text = "Older legacy", Model = "Legacy model", Kind = "Repolish" }
            } }
        } }));
        var store = new TranscriptHistoryStore(HistoryPath);
        store.Load();
        Assert.Empty(store.Entries[0].RawText);
        Assert.Null(store.Entries[0].Provider);
        Assert.Null(store.Entries[0].Revisions[0].Provider);
        store.RestorePrevious(id);
        Assert.Equal("Older legacy", store.Entries[0].Text);
        Assert.Equal("Legacy model", store.Entries[0].Model);
        Assert.Null(store.Entries[0].Reasoning);
        using var saved = JsonDocument.Parse(File.ReadAllText(HistoryPath));
        var entry = saved.RootElement.GetProperty("Entries")[0];
        Assert.False(entry.TryGetProperty("Preview", out _));
        Assert.False(entry.TryGetProperty("DisplayDate", out _));
        Assert.False(entry.TryGetProperty("Detail", out _));
    }

    [Fact] public void OversizedSerializedSavePreservesReloadableDiskAndMemoryAndCleansTemporaryFile()
    {
        // Revisions permit much more text than the old primary-text-only budget.
        // Seed one near-limit valid file directly, avoiding repeated large saves.
        Directory.CreateDirectory(directory);
        string text = new('x', 100_000);
        var revisions = Enumerable.Range(0, 10).Select(_ => new TranscriptRevision(DateTimeOffset.UtcNow, text, "Local", "Repolish")).ToArray();
        var entries = Enumerable.Range(0, 111).Select(_ => new TranscriptEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, text, "Clean") {
            RawText = text, Revisions = revisions
        }).ToArray();
        using (var output = File.Create(HistoryPath)) JsonSerializer.Serialize(output, new { Version = 1, Entries = entries });
        long length = new FileInfo(HistoryPath).Length;
        Assert.InRange(length, 127L * 1024 * 1024, 128L * 1024 * 1024);
        byte[] digest;
        using (var input = File.OpenRead(HistoryPath)) digest = SHA256.HashData(input);
        var store = new TranscriptHistoryStore(HistoryPath);
        store.Load();
        var snapshot = store.Entries;
        // JSON escapes this non-ASCII text, so the legal entry crosses the byte cap.
        string unicode = new('\u00e9', 100_000);
        Assert.Throws<InvalidDataException>(() => store.Add(unicode, "Clean", unicode));
        Assert.Same(snapshot, store.Entries);
        Assert.Equal(length, new FileInfo(HistoryPath).Length);
        using (var input = File.OpenRead(HistoryPath)) Assert.Equal(digest, SHA256.HashData(input));
        Assert.Single(Directory.GetFiles(directory));
        // Existing data can still be reduced and saved, and that successful save reloads.
        store.Remove(store.Entries[0].Id);
        var reloaded = new TranscriptHistoryStore(HistoryPath);
        reloaded.Load();
        Assert.Equal(110, reloaded.Entries.Count);
        Assert.Equal(store.Entries[0].Revisions.ToArray(), reloaded.Entries[0].Revisions.ToArray());
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
