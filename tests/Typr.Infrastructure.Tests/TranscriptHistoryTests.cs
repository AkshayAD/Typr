using System.Text.Json;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

public sealed class TranscriptHistoryTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "typr-history-tests-" + Guid.NewGuid().ToString("N"));
    string HistoryPath => Path.Combine(directory, "history.json");

    [Fact] public void MissingHistoryIsEmptyAndRoundtripPreservesLiteralTextAndMode()
    {
        var store = new TranscriptHistoryStore(HistoryPath);
        store.Load();
        Assert.Empty(store.Entries);
        store.Add("First result.", "Clean");
        store.Add("Literal $1\nSecond line — café", "Raw");
        var reloaded = new TranscriptHistoryStore(HistoryPath);
        reloaded.Load();
        Assert.Equal(store.Entries, reloaded.Entries);
        Assert.Equal("Raw", reloaded.Entries[0].Mode);
        Assert.Equal("Literal $1\nSecond line — café", reloaded.Entries[0].Text);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact] public void RetainsOnlyNewestTwoHundredAndSupportsRemoveAndClear()
    {
        var store = new TranscriptHistoryStore(HistoryPath);
        for (int i = 0; i < 203; i++) store.Add($"Result {i}", "Clean");
        Assert.Equal(200, store.Entries.Count);
        Assert.Equal("Result 202", store.Entries[0].Text);
        Assert.Equal("Result 3", store.Entries[^1].Text);
        store.Remove(store.Entries[0].Id);
        Assert.Equal(199, store.Entries.Count);
        var reloaded = new TranscriptHistoryStore(HistoryPath);
        reloaded.Load();
        Assert.Equal(store.Entries, reloaded.Entries);
        reloaded.Clear();
        store.Load();
        Assert.Empty(store.Entries);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":2,\"Entries\":[]}")]
    [InlineData("{\"Version\":1,\"Entries\":null}")]
    [InlineData("{\"Version\":1,\"Entries\":[null]}")]
    public void UnreadableHistoryIsPreservedAndCannotBeOverwrittenByMutation(string original)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(HistoryPath, original);
        var store = new TranscriptHistoryStore(HistoryPath);
        Assert.ThrowsAny<Exception>(store.Load);
        Assert.ThrowsAny<Exception>(() => store.Add("New result", "Clean"));
        Assert.ThrowsAny<Exception>(store.Clear);
        Assert.Empty(store.Entries);
        Assert.Equal(original, File.ReadAllText(HistoryPath));
    }

    [Fact] public void RejectsInvalidEntriesAndDuplicateIdsWithoutChangingPriorSnapshot()
    {
        var store = new TranscriptHistoryStore(HistoryPath);
        store.Add("Existing result", "Clean");
        var original = store.Entries[0];
        foreach (var entries in new[] {
            new[] { original, original },
            new[] { original with { Id = Guid.Empty } },
            new[] { original with { CreatedAt = default } },
            new[] { original with { Mode = "Unknown" } },
            new[] { original with { Text = " " } },
            new[] { original with { Text = new string('x', 100_001) } },
            Enumerable.Range(0, 201).Select(_ => original with { Id = Guid.NewGuid() }).ToArray()
        }) {
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
            Assert.Throws<InvalidDataException>(store.Load);
            Assert.Equal(original, Assert.Single(store.Entries));
        }
    }

    [Fact] public void FailedWritePreservesMemoryAndLeavesNoTemporaryFile()
    {
        var store = new TranscriptHistoryStore(HistoryPath);
        store.Add("Existing result", "Clean");
        var original = File.ReadAllText(HistoryPath);
        using (var locked = new FileStream(HistoryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
            Assert.ThrowsAny<IOException>(() => store.Add("New result", "Raw"));
            Assert.Equal("Existing result", Assert.Single(store.Entries).Text);
            Assert.Single(Directory.GetFiles(directory));
        }
        Assert.Equal(original, File.ReadAllText(HistoryPath));
        store.Add("Retry result", "Raw");
        Assert.Equal(2, store.Entries.Count);
    }

    [Fact] public void LoadedEntriesAreNewestFirstAndPublishedSnapshotsAreReadOnly()
    {
        Directory.CreateDirectory(directory);
        var earlier = new TranscriptEntry(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), "Earlier", "Clean");
        var later = new TranscriptEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "Later", "Raw");
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(new { Version = 1, Entries = new[] { earlier, later } }));
        var store = new TranscriptHistoryStore(HistoryPath);
        store.Load();
        var snapshot = store.Entries;
        Assert.Equal(new[] { later, earlier }, snapshot);
        Assert.Throws<NotSupportedException>(() => ((IList<TranscriptEntry>)snapshot).Clear());
        store.Clear();
        Assert.Equal(2, snapshot.Count);
        Assert.Empty(store.Entries);
    }

    [Theory]
    [InlineData("", "Clean")]
    [InlineData(" ", "Raw")]
    [InlineData("Result", "Unknown")]
    public void RejectsUnusableNewResultsBeforeCreatingAFile(string text, string mode)
    {
        var store = new TranscriptHistoryStore(HistoryPath);
        Assert.Throws<ArgumentException>(() => store.Add(text, mode));
        Assert.False(Directory.Exists(directory));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
