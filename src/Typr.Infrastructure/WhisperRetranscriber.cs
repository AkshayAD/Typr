using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;
using SherpaOnnx;

namespace Typr.Infrastructure;

public sealed record RetranscriptionModel(string Id, string Name, string Description, long DownloadBytes);

/// <summary>Optional, local-only Whisper transcription of explicitly retained recordings.</summary>
public sealed class WhisperRetranscriber
{
    internal const int ChunkSamples = 25 * 16000;
    private readonly string rootDirectory;
    private readonly SemaphoreSlim operation = new(1, 1);
    private sealed record ModelFile(string Name, long Size, string Hash);
    private sealed record Manifest(string Id, string Repository, string Revision, int FeatureDim, ModelFile[] Files);
    // Pinned Hugging Face revisions; hashes are the published LFS SHA256 values
    // and SHA256 of the corresponding token file, verified before any native load.
    private static readonly Manifest[] Manifests = [
        new("whisper-small", "csukuangfj/sherpa-onnx-whisper-small", "8f3c18b358db4d1f2fc1eae49d75cd20989e4309", 80, [
            new("small-encoder.int8.onnx", 112442483, "4cbe7b22fa9026b843b60a68640c747de05bafb1a11b57edc0e66c232d9f33a9"),
            new("small-decoder.int8.onnx", 262226114, "acad50b5c782696e91b55914cc5ab4f756f1532f76e22aa6fc615f39fb69a8ee"),
            new("small-tokens.txt", 816730, "b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126")]),
        new("whisper-large-v3", "csukuangfj/sherpa-onnx-whisper-large-v3", "2a6507094dd6020d939d78e3f1834a1d06267fca", 128, [
            new("large-v3-encoder.int8.onnx", 766671985, "d531cf17248acc43e8c09b472a0877055e770877857a5332fc1304b36534ec85"),
            new("large-v3-decoder.int8.onnx", 1008265203, "ebc6bfd88e162a46cb3edee8a7e727e1dcbc65cabecb19e2573695e4d495e1af"),
            new("large-v3-tokens.txt", 816730, "b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126")])
    ];

    public static IReadOnlyList<RetranscriptionModel> Models { get; } = Array.AsReadOnly(new[] {
        new RetranscriptionModel(Manifests[0].Id, "Whisper Small · multilingual", "Recommended for CPU. Local multilingual transcription with a smaller download.", Manifests[0].Files.Sum(file => file.Size)),
        new RetranscriptionModel(Manifests[1].Id, "Whisper Large v3 · multilingual", "Larger quality-focused model. Much slower and uses more memory on CPU.", Manifests[1].Files.Sum(file => file.Size))
    });

    public WhisperRetranscriber(string? rootDirectory = null)
        => this.rootDirectory = rootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typr", "models");

    /// <summary>Fast presence check. Transcription always verifies the complete file hashes.</summary>
    public bool IsReady(string id)
    {
        var manifest = FindModel(id);
        try { return manifest.Files.All(file => File.Exists(ModelPath(manifest, file)) && new FileInfo(ModelPath(manifest, file)).Length == file.Size); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task DownloadAsync(string id, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var manifest = FindModel(id);
        cancellationToken.ThrowIfCancellationRequested();
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.Combine(rootDirectory, manifest.Id));
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            long completed = 0, total = manifest.Files.Sum(file => file.Size);
            foreach (var file in manifest.Files)
            {
                await DownloadFileAsync(client, new Uri($"https://huggingface.co/{manifest.Repository}/resolve/{manifest.Revision}/{file.Name}"),
                    ModelPath(manifest, file), file.Size, file.Hash,
                    bytes => progress?.Report((double)(completed + bytes) / total), cancellationToken).ConfigureAwait(false);
                completed += file.Size;
            }
            progress?.Report(1);
        }
        finally { operation.Release(); }
    }

    /// <summary>
    /// Recognizes PCM16 mono 16 kHz WAV locally, in chunks of at most 25 seconds.
    /// Cancellation is checked before and after native model loading/decoding;
    /// an individual CPU inference call cannot be interrupted safely.
    /// </summary>
    public async Task<string> TranscribeAsync(string audioPath, string id, int threads, CancellationToken cancellationToken = default)
    {
        var manifest = FindModel(id);
        cancellationToken.ThrowIfCancellationRequested();
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var audio = OpenAudio(audioPath);
            foreach (var file in manifest.Files)
            {
                if (!await VerifyFileAsync(ModelPath(manifest, file), file.Size, file.Hash, cancellationToken).ConfigureAwait(false))
                    throw new FileNotFoundException("The selected Whisper model is missing or failed its integrity check. Download it before re-transcribing.");
            }
            return await Task.Run(() => Decode(audio, manifest, threads, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { operation.Release(); }
    }

    private string Decode(WaveFileReader audio, Manifest manifest, int threads, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = manifest.FeatureDim;
        config.ModelConfig.Whisper.Encoder = ModelPath(manifest, manifest.Files[0]);
        config.ModelConfig.Whisper.Decoder = ModelPath(manifest, manifest.Files[1]);
        config.ModelConfig.Whisper.Language = ""; // Auto-detect; preserve the spoken language.
        config.ModelConfig.Whisper.Task = "transcribe";
        config.ModelConfig.Tokens = ModelPath(manifest, manifest.Files[2]);
        config.ModelConfig.NumThreads = Math.Clamp(threads, 1, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        using var recognizer = new OfflineRecognizer(config);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new StringBuilder();
        foreach (var samples in ReadChunks(audio, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples);
            cancellationToken.ThrowIfCancellationRequested();
            recognizer.Decode(stream);
            cancellationToken.ThrowIfCancellationRequested();
            string text = stream.Result.Text.Trim();
            if (text.Length == 0) continue;
            if (result.Length > 0) result.Append(' ');
            result.Append(text);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result.ToString();
    }

    internal static WaveFileReader OpenAudio(string path)
    {
        var reader = new WaveFileReader(path);
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.SampleRate != 16000 ||
            reader.WaveFormat.Channels != 1 || reader.WaveFormat.BitsPerSample != 16 || reader.Length == 0 || reader.Length % 2 != 0)
        {
            reader.Dispose();
            throw new InvalidDataException("Re-transcription requires a nonempty PCM16 mono 16 kHz WAV recording.");
        }
        return reader;
    }

    internal static IEnumerable<float[]> ReadChunks(WaveFileReader audio, CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkSamples * 2];
        while (audio.Position < audio.Length)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = audio.Read(buffer, read, buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            if (read == 0 || read % 2 != 0) throw new InvalidDataException("The recording ended before a complete audio sample could be read.");
            var samples = new float[read / 2];
            for (int index = 0; index < samples.Length; index++)
                samples[index] = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(index * 2, 2)) / 32768f;
            yield return samples;
        }
    }

    private static Manifest FindModel(string id) => Manifests.FirstOrDefault(model => model.Id == id)
        ?? throw new ArgumentOutOfRangeException(nameof(id), "Choose a supported Whisper model.");
    private string ModelPath(Manifest manifest, ModelFile file) => Path.Combine(rootDirectory, manifest.Id, file.Name);

    internal static async Task<bool> VerifyFileAsync(string path, long size, string hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actual = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(actual).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task DownloadFileAsync(HttpClient client, Uri source, string destination, long size, string hash,
        Action<long> progress, CancellationToken cancellationToken)
    {
        if (await VerifyFileAsync(destination, size, hash, cancellationToken).ConfigureAwait(false)) { progress(size); return; }
        string partial = destination + ".part";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > size) { File.Delete(partial); offset = 0; }
        progress(offset);
        if (offset < size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != offset || range.Length is { } length && length != size)
                    throw new IOException("The download resume position or expected size did not match.");
            }
            else offset = 0; // A server that ignores Range must replace the partial file.
            await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (offset + read > size) throw new IOException("The model download exceeded its pinned size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    offset += read; progress(offset);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < size) throw new IOException("The model download was interrupted. Retry to resume it.");
        if (!await VerifyFileAsync(partial, size, hash, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new IOException("The model failed its SHA256 integrity check. Retry the download.");
        }
        File.Move(partial, destination, overwrite: true);
    }
}
