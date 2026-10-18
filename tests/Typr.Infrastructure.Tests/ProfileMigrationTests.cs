using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

public sealed class ProfileMigrationTests : IDisposable
{
    readonly string parent = Path.Combine(Path.GetTempPath(), "typr-migration-tests-" + Guid.NewGuid().ToString("N"));
    string Source => Path.Combine(parent, ProfileMigration.LegacyDirectoryName);
    string Destination => Path.Combine(parent, "Typr");

    [Fact] public void CopiesNestedDataAtomicallyAndPreservesTheSource()
    {
        Directory.CreateDirectory(Path.Combine(Source, "models", "synthetic"));
        File.WriteAllText(Path.Combine(Source, "settings.json"), "synthetic settings");
        File.WriteAllBytes(Path.Combine(Source, "models", "synthetic", "model.bin"), new byte[] { 1, 2, 3, 4 });
        Assert.Equal(Destination, ProfileMigration.MigrateDirectory(parent));
        Assert.Equal("synthetic settings", File.ReadAllText(Path.Combine(Destination, "settings.json")));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(Destination, "models", "synthetic", "model.bin")));
        Assert.Equal("synthetic settings", File.ReadAllText(Path.Combine(Source, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Source, "models", "synthetic", "model.bin")));
        Assert.Empty(Directory.GetDirectories(parent, "Typr.migration-*"));
    }

    [Fact] public void ExistingDestinationIsNeverMergedOrOverwrittenAndMigrationIsIdempotent()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "settings.json"), "original source");
        ProfileMigration.MigrateDirectory(parent);
        File.WriteAllText(Path.Combine(Destination, "settings.json"), "new profile choice");
        File.WriteAllText(Path.Combine(Source, "source-only.txt"), "do not merge");
        Assert.Equal(Destination, ProfileMigration.MigrateDirectory(parent));
        Assert.Equal("new profile choice", File.ReadAllText(Path.Combine(Destination, "settings.json")));
        Assert.False(File.Exists(Path.Combine(Destination, "source-only.txt")));
        Assert.Equal("original source", File.ReadAllText(Path.Combine(Source, "settings.json")));
    }

    [Fact] public void MissingSourceReturnsTheNewLocationWithoutCreatingOrDeletingData()
    {
        Assert.Equal(Destination, ProfileMigration.MigrateDirectory(parent));
        Assert.False(Directory.Exists(parent));
    }

    [Fact] public void ExistingDestinationFileIsPreservedAndReportedAsAControlledFailure()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Destination, "existing destination file");
        File.WriteAllText(Path.Combine(Source, "settings.json"), "original settings");
        Assert.Throws<IOException>(() => ProfileMigration.MigrateDirectory(parent));
        Assert.Equal("existing destination file", File.ReadAllText(Destination));
        Assert.Equal("original settings", File.ReadAllText(Path.Combine(Source, "settings.json")));
    }

    [Fact] public void LockedSourceFailsWithControlledErrorAndLeavesNoPartialDestination()
    {
        Directory.CreateDirectory(Source);
        var source = Path.Combine(Source, "settings.json");
        File.WriteAllText(source, "preserve me");
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            var error = Assert.Throws<IOException>(() => ProfileMigration.MigrateDirectory(parent));
            Assert.Contains("original files are preserved", error.Message);
            Assert.False(Directory.Exists(Destination));
            Assert.Empty(Directory.GetDirectories(parent, "Typr.migration-*"));
        }
        Assert.Equal("preserve me", File.ReadAllText(source));
        ProfileMigration.MigrateDirectory(parent);
        Assert.Equal("preserve me", File.ReadAllText(Path.Combine(Destination, "settings.json")));
    }

    [Fact] public void ExcessiveDepthFailsWithoutPublishingPartialData()
    {
        var deepest = Source;
        for (int i = 0; i < 34; i++) deepest = Path.Combine(deepest, "d");
        Directory.CreateDirectory(deepest);
        File.WriteAllText(Path.Combine(deepest, "synthetic.txt"), "preserve me");
        Assert.Throws<IOException>(() => ProfileMigration.MigrateDirectory(parent));
        Assert.False(Directory.Exists(Destination));
        Assert.True(File.Exists(Path.Combine(deepest, "synthetic.txt")));
        Assert.Empty(Directory.GetDirectories(parent, "Typr.migration-*"));
    }

    [Fact] public void ModelPathsMoveOnlyWithinTheCopiedModelsTreeWhenTheEquivalentExists()
    {
        var oldFolder = Path.Combine(Source, "models", "synthetic");
        Directory.CreateDirectory(oldFolder);
        var oldFile = Path.Combine(oldFolder, "model.bin");
        File.WriteAllText(oldFile, "model");
        var newFolder = Path.Combine(Destination, "models", "synthetic");
        Assert.Equal(oldFolder, ProfileMigration.NormalizeModelPath(oldFolder, Source, Destination));
        ProfileMigration.MigrateDirectory(parent);
        Assert.Equal(newFolder, ProfileMigration.NormalizeModelPath(oldFolder, Source, Destination));
        Assert.Equal(Path.Combine(newFolder, "model.bin"), ProfileMigration.NormalizeModelPath(oldFile, Source, Destination));
        var outside = Path.Combine(Source, "logs", "model.bin");
        var traversed = Path.Combine(Source, "models", "..", "logs", "model.bin");
        Assert.Equal(outside, ProfileMigration.NormalizeModelPath(outside, Source, Destination));
        Assert.Equal(traversed, ProfileMigration.NormalizeModelPath(traversed, Source, Destination));
        Assert.Equal("models/relative", ProfileMigration.NormalizeModelPath("models/relative", Source, Destination));
        Assert.Null(ProfileMigration.NormalizeModelPath(null, Source, Destination));
    }

    public void Dispose() { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
}
