using Typr.Infrastructure;
using NAudio.Wave;
using Xunit;

namespace Typr.Infrastructure.Tests;

public sealed class AudioRetentionTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "typr-audio-tests-" + Guid.NewGuid().ToString("N"));

    [Fact] public void DisabledRetentionCreatesNoDirectoryOrFile()
    {
        Assert.Null(SavedAudioRecording.Start(directory, 0));
        Assert.Null(SavedAudioRecording.Start(directory, -1));
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public void CancelledOrFaultedRecordingIsDiscardedWithoutRemovingPriorAudio()
    {
        using (var retained = SavedAudioRecording.Start(directory, 1)!) {
            retained.WriteSamples(new[] { 0.1f, 0.2f });
            retained.Complete();
        }
        var original = Assert.Single(Directory.GetFiles(directory));
        using (var cancelled = SavedAudioRecording.Start(directory, 1)!) {
            cancelled.WriteSamples(new[] { 0.3f });
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        Assert.Equal(original, Assert.Single(Directory.GetFiles(directory)));
    }

    [Fact] public void SuccessfulRecordingsEnforceConfiguredBoundAndContainReadablePcm()
    {
        for (int i = 0; i < 4; i++) {
            using var recording = SavedAudioRecording.Start(directory, 2)!;
            recording.WriteSamples(new[] { 0.1f, -0.2f, 0.3f });
            recording.Complete();
        }
        var files = Directory.GetFiles(directory);
        Assert.Equal(2, files.Length);
        foreach (var file in files) {
            using var wave = new WaveFileReader(file);
            Assert.Equal(16000, wave.WaveFormat.SampleRate);
            Assert.Equal(1, wave.WaveFormat.Channels);
            Assert.Equal(16, wave.WaveFormat.BitsPerSample);
            Assert.Equal(6, wave.Length);
        }
    }

    [Fact] public void LimitOneKeepsCurrentRecordingWhenFileTimestampsTie()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "old.wav"), Array.Empty<byte>());
        using (var recording = SavedAudioRecording.Start(directory, 1)!) {
            foreach (var file in Directory.GetFiles(directory)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.Date);
            recording.WriteSamples(new[] { 0.1f });
            recording.Complete();
        }
        Assert.NotEqual("old.wav", Path.GetFileName(Assert.Single(Directory.GetFiles(directory))));
    }

    [Fact] public void FailedRetentionCleanupDiscardsNewRecording()
    {
        Directory.CreateDirectory(directory);
        var prior = Path.Combine(directory, "old.wav");
        using (var locked = new FileStream(prior, FileMode.Create, FileAccess.Write, FileShare.None)) {
            using (var recording = SavedAudioRecording.Start(directory, 1)!) {
                recording.WriteSamples(new[] { 0.1f });
                Assert.Throws<IOException>(recording.Complete);
            }
            Assert.Equal(prior, Assert.Single(Directory.GetFiles(directory)));
        }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
