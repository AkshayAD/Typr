using NAudio.Wave;

namespace Ava.Infrastructure;

/// <summary>An opt-in recording is retained only after recognition completes successfully.</summary>
internal sealed class SavedAudioRecording : IDisposable
{
    readonly string directory;
    readonly string filePath;
    readonly int limit;
    WaveFileWriter? writer;
    bool completed;

    SavedAudioRecording(string directory, int limit)
    {
        this.directory = directory;
        this.limit = limit;
        Directory.CreateDirectory(directory);
        filePath = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.wav");
        writer = new WaveFileWriter(filePath, new WaveFormat(16000, 16, 1));
    }

    public static SavedAudioRecording? Start(string directory, int limit) =>
        limit <= 0 ? null : new SavedAudioRecording(directory, Math.Clamp(limit, 1, 200));

    public void WriteSamples(float[] samples) => writer!.WriteSamples(samples, 0, samples.Length);

    public void Complete()
    {
        writer?.Dispose();
        writer = null;
        // Keep this recording plus the newest prior recordings, even when timestamps tie.
        foreach (var file in Directory.EnumerateFiles(directory, "*.wav")
                     .Where(file => !string.Equals(file, filePath, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(file => file, StringComparer.Ordinal)
                     .Skip(limit - 1)) File.Delete(file);
        completed = true;
    }

    public void Dispose()
    {
        try { writer?.Dispose(); }
        finally {
            writer = null;
            if (!completed && File.Exists(filePath)) File.Delete(filePath);
        }
    }
}
