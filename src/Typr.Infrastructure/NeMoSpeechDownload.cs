using System.IO.Compression;

namespace Typr.Infrastructure;

/// <summary>Optional, private CUDA speech files; never installs a system toolkit or changes PATH.</summary>
public static class NeMoSpeechDownload
{
    public const string ModelFileName = "model.gguf";
    public const string NativeDirectoryName = "native";
    internal const string ArchiveRoot = "nemo-speech-0.2.0-windows-x86_64-cuda";
    internal const long ArchiveSize = 102183465;
    internal const string ArchiveHash = "9ffe5e842c39a7d3211f3b2c3b3aa1bca8c0b2455d96d961ed5f22ec5108f98d";
    internal const long ModelSize = 699872960;
    internal const string ModelHash = "d9a01898d2a611c8764e23a1c2f45e70bbd5a425dc4de93692ac951dd603812d";
    private const string ArchiveUrl = "https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.2.0/" + ArchiveRoot + ".zip";
    private const string ModelUrl = "https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b/resolve/ebe59e5a817142986528bbbee5dba8db7b38ed50/nemotron-speech-streaming-en-0.6b.q8_0.gguf";
    private const long DiskHeadroom = 64L * 1024 * 1024;
    private static readonly SemaphoreSlim Operation = new(1, 1);
    internal sealed record BundleFile(string ArchivePath, string RelativePath, long Size, string Hash);
    // Pinned official v0.2.0 ZIP contents. Only ASR, its native/runtime dependencies
    // and upstream license notices are extracted; no CLI, NMT or TTS components.
    internal static IReadOnlyList<BundleFile> BundleFiles { get; } = Array.AsReadOnly(new BundleFile[] {
        new("bin/concrt140.dll", "native/concrt140.dll", 324208, "2405355f0a58067b258f8df33c327e3a3d716eaac5a3a5aebb757842d85bd376"),
        new("bin/cublas64_13.dll", "native/cublas64_13.dll", 1605120, "a129dc892fc2524c43b0656549bed94f9bab23f220cd57d67e334ad717bea12b"),
        new("bin/ggml-base.dll", "native/ggml-base.dll", 680960, "21747a2010b6e13f44d264f1746ca900e42b02175a72e7f9257ce7b991403513"),
        new("bin/ggml-cpu.dll", "native/ggml-cpu.dll", 946688, "1b1c8b11520860d0aae09528247dc3d009d8b617d5426e8ba088b1fd00f53ca4"),
        new("bin/ggml-cuda.dll", "native/ggml-cuda.dll", 106126848, "b8473ac9477878af0fab5d6f082900142e77d0a85f3fe1d1d17fa4cbb64e4b74"),
        new("bin/ggml.dll", "native/ggml.dll", 61952, "18f3ac07e20375deb0548aa46d387930ad15713f181570acfc9078a47cce8f9e"),
        new("bin/msvcp140.dll", "native/msvcp140.dll", 557728, "0f885b509a685d2bbfa652fed26b5fb31d88fbdab0a978c641d1c7b8aa460aa9"),
        new("bin/msvcp140_1.dll", "native/msvcp140_1.dll", 35952, "bfad5aef4c63a669e3c140655cdfdf395b6c979b400a447bd5dcb65ed8826c3d"),
        new("bin/msvcp140_2.dll", "native/msvcp140_2.dll", 280200, "3ea06f0ee098b4823cb79599df3780e7f23cce52c19aac31d2a0d47efe33a5e9"),
        new("bin/msvcp140_atomic_wait.dll", "native/msvcp140_atomic_wait.dll", 50304, "640b2aefced484d0368eea5bdd06addd0658a3a70a49256e560d6923b404a479"),
        new("bin/msvcp140_codecvt_ids.dll", "native/msvcp140_codecvt_ids.dll", 31872, "f2069a52880ec885ee7f0511186100eb7fada0411a2b4948fafea7735b878a18"),
        new("bin/nemo_speech_asr.dll", "native/nemo_speech_asr.dll", 4209152, "0acba5cec6334342d8655dca572a5a684cad310be99b4cb0fda662452626c42e"),
        new("bin/nemo_speech_asr_c.dll", "native/nemo_speech_asr_c.dll", 56320, "2cd5e8d75815f1bae4eb92101fca769b3eca3b03626f7b84c54a0babfe0074d3"),
        new("bin/vcomp140.dll", "native/vcomp140.dll", 193152, "55aba23cdcd6484fbb06f4155b8ca75adfce7a881f10afd0c49457165e677164"),
        new("bin/vcruntime140.dll", "native/vcruntime140.dll", 124544, "d5e4d9a3e835fa679450145d6a7d94e36573a509317111904d9b3712c30d9066"),
        new("bin/vcruntime140_1.dll", "native/vcruntime140_1.dll", 49792, "1f2d41c4aa5db0bc33ebf7b66d72943a817d7ce6cbe880502a9403823633093f"),
        new("share/licenses/nemo-speech/LICENSE", "licenses/nemo-speech/LICENSE", 12454, "6eb0d1526ef4d4e4f5516c6d679eb148aefc36d0d2df9d9cc1fa048fa287963c"),
        new("share/licenses/nemo-speech/NOTICE", "licenses/nemo-speech/NOTICE", 239, "0f55f591903fe80a07f8b1331029983b2b9f460967ccfb8dbe60f958958bec25"),
        new("share/licenses/nemo-speech/THIRD_PARTY_NOTICES.md", "licenses/nemo-speech/THIRD_PARTY_NOTICES.md", 10527, "3b3163ab04d77bd137f5823d5dda5328448f43fe34ed7780edc8148b62b454c6"),
        new("share/licenses/nemo-speech/third_party/cpp-httplib/LICENSE", "licenses/nemo-speech/third_party/cpp-httplib/LICENSE", 1097, "bb546eae5924d8ee842e0e567e6d533c5824bdebccf605f99a5b3429034e6697"),
        new("share/licenses/nemo-speech/third_party/ggml/LICENSE", "licenses/nemo-speech/third_party/ggml/LICENSE", 1099, "bcd8ec749126d45cb06737d0690295d73df4b6e7e194205bcf91190368f27285"),
        new("share/licenses/nemo-speech/third_party/llama.cpp/LICENSE", "licenses/nemo-speech/third_party/llama.cpp/LICENSE", 1099, "bcd8ec749126d45cb06737d0690295d73df4b6e7e194205bcf91190368f27285"),
        new("share/licenses/nemo-speech/third_party/miniaudio/LICENSE", "licenses/nemo-speech/third_party/miniaudio/LICENSE", 2645, "85c4a6fe037f937d5cd3328e1630ec7c0a3338ac639dca9a4face955be9a71db"),
        new("share/licenses/nemo-speech/third_party/vcpkg/abseil/LICENSE", "licenses/nemo-speech/third_party/vcpkg/abseil/LICENSE", 11361, "c79a7fea0e3cac04cd43f20e7b648e5a0ff8fa5344e644b0ee09ca1162b62747"),
        new("share/licenses/nemo-speech/third_party/vcpkg/protobuf/LICENSE", "licenses/nemo-speech/third_party/vcpkg/protobuf/LICENSE", 1732, "6e5e117324afd944dcf67f36cf329843bc1a92229a8cd9bb573d7a83130fea7d"),
        new("share/licenses/nemo-speech/third_party/vcpkg/sentencepiece/LICENSE", "licenses/nemo-speech/third_party/vcpkg/sentencepiece/LICENSE", 11358, "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30"),
        new("share/licenses/nemo-speech/third_party/vcpkg/utf8-range/LICENSE", "licenses/nemo-speech/third_party/vcpkg/utf8-range/LICENSE", 1091, "02de69b64fc36d9e938f418e52723e42f0b2b226d58a9cb3c8dcbdf7059f5074")
    });

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typr", "models", "nemotron-cuda");
    public static long DownloadBytes => ArchiveSize + ModelSize;
    public static long RequiredDiskBytes => DownloadBytes + BundleFiles.Sum(file => file.Size) + DiskHeadroom;

    /// <summary>Fast size/presence check; VerifyAsync must succeed before loading native code.</summary>
    public static bool IsReady(string directory)
    {
        try
        {
            string root = Path.GetFullPath(directory);
            return HasSize(SafePath(root, ModelFileName), ModelSize) &&
                BundleFiles.All(file => HasSize(SafePath(root, file.RelativePath), file.Size));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static async Task<bool> VerifyAsync(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string root = Path.GetFullPath(directory);
            if (!await ValidAsync(SafePath(root, ModelFileName), ModelSize, ModelHash, cancellationToken).ConfigureAwait(false)) return false;
            foreach (var file in BundleFiles)
                if (!await ValidAsync(SafePath(root, file.RelativePath), file.Size, file.Hash, cancellationToken).ConfigureAwait(false)) return false;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static async Task DownloadAsync(string directory, IProgress<double>? progress = null, CancellationToken cancellationToken = default, ModelLocations? locations = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string root = Path.GetFullPath(directory);
            EnsureNoReparsePoints(root);
            Directory.CreateDirectory(root);
            // Also serialize independent processes sharing a custom model directory.
            await using var downloadLock = new FileStream(SafePath(root, ".download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (await VerifyAsync(root, cancellationToken).ConfigureAwait(false)) {
                (locations ?? new ModelLocations()).RememberVerifiedGpu(root);
                progress?.Report(1); return;
            }
            string model = SafePath(root, ModelFileName);
            string archive = SafePath(root, ArchiveRoot + ".zip");
            long needed = await RemainingDownloadAsync(model, ModelSize, ModelHash, cancellationToken).ConfigureAwait(false);
            bool runtimeReady = true;
            foreach (var file in BundleFiles)
                if (!await ValidAsync(SafePath(root, file.RelativePath), file.Size, file.Hash, cancellationToken).ConfigureAwait(false))
                {
                    runtimeReady = false;
                    needed += file.Size;
                }
            if (!runtimeReady) needed += await RemainingDownloadAsync(archive, ArchiveSize, ArchiveHash, cancellationToken).ConfigureAwait(false);
            long available = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
            if (available < needed + DiskHeadroom)
                throw new IOException($"GPU speech setup needs about {Math.Ceiling((needed + DiskHeadroom) / (1024d * 1024 * 1024)):0.0} GB of free space on the selected drive. Choose another folder or free space, then retry; partial downloads are kept.");

            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            if (!runtimeReady)
            {
                await DownloadFileAsync(client, new Uri(ArchiveUrl), archive, ArchiveSize, ArchiveHash,
                    bytes => progress?.Report((double)bytes / DownloadBytes), cancellationToken).ConfigureAwait(false);
                using var zip = ZipFile.OpenRead(archive);
                await ExtractVerifiedAsync(zip, root, ArchiveRoot, BundleFiles, cancellationToken).ConfigureAwait(false);
            }
            await DownloadFileAsync(client, new Uri(ModelUrl), model, ModelSize, ModelHash,
                bytes => progress?.Report((double)(ArchiveSize + bytes) / DownloadBytes), cancellationToken).ConfigureAwait(false);
            if (!await VerifyAsync(root, cancellationToken).ConfigureAwait(false)) throw new IOException("GPU speech files failed verification. Retry setup to repair the affected files.");
            (locations ?? new ModelLocations()).RememberVerifiedGpu(root);
            progress?.Report(1);
        }
        finally { Operation.Release(); }
    }

    // Reuse the existing bounded, SHA256-checked HTTP resume implementation.
    internal static async Task DownloadFileAsync(HttpClient client, Uri source, string destination, long size, string hash, Action<long> progress, CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(destination);
        EnsureNoReparsePoints(destination + ".part");
        await WhisperRetranscriber.DownloadFileAsync(client, source, destination, size, hash, progress, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ExtractVerifiedAsync(ZipArchive archive, string directory, string archiveRoot, IReadOnlyList<BundleFile> files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(directory);
        EnsureNoReparsePoints(root);
        if (archive.Entries.Count > 256) throw new InvalidDataException("GPU runtime archive has too many entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidateArchivePath(entry.FullName);
            if (!entry.FullName.StartsWith(archiveRoot + "/", StringComparison.Ordinal) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                !entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("GPU runtime archive contains an unsafe or duplicate entry.");
        }
        // Validate the complete selected set before replacing any existing file.
        foreach (var file in files)
        {
            if (!entries.TryGetValue(archiveRoot + "/" + file.ArchivePath, out var entry) || entry.Length != file.Size)
                throw new InvalidDataException($"GPU runtime archive is missing the expected {file.ArchivePath}.");
            SafePath(root, file.RelativePath);
        }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string final = SafePath(root, file.RelativePath);
            if (await ValidAsync(final, file.Size, file.Hash, cancellationToken).ConfigureAwait(false)) continue;
            string partial = SafePath(root, file.RelativePath + ".extract.part");
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var input = entries[archiveRoot + "/" + file.ArchivePath].Open())
            {
                byte[] buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (written + read > file.Size) throw new InvalidDataException("GPU runtime archive exceeded its pinned file size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                }
            }
            if (!await ValidAsync(partial, file.Size, file.Hash, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(partial);
                throw new InvalidDataException($"GPU runtime integrity check failed for {file.RelativePath}.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(final);
            File.Move(partial, final, overwrite: true);
        }
    }

    private static void ValidateArchivePath(string name)
    {
        string trimmed = name.TrimEnd('/');
        if (trimmed.Length == 0 || trimmed.Split('/').Any(part => part.Length == 0 || part is "." or ".." ||
            part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0))
            throw new InvalidDataException("GPU runtime archive contains an unsafe path.");
    }

    internal static string SafePath(string root, string relativePath)
    {
        ValidateArchivePath(relativePath);
        string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(basePath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("GPU speech path escaped the selected folder.");
        EnsureNoReparsePoints(path);
        return path;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("GPU speech setup cannot use a symbolic link or junction. Choose a regular folder.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool HasSize(string path, long size) => File.Exists(path) && new FileInfo(path).Length == size;
    private static Task<bool> ValidAsync(string path, long size, string hash, CancellationToken ct)
    {
        EnsureNoReparsePoints(path);
        return WhisperRetranscriber.VerifyFileAsync(path, size, hash, ct);
    }

    private static async Task<long> RemainingDownloadAsync(string destination, long size, string hash, CancellationToken ct)
    {
        if (await ValidAsync(destination, size, hash, ct).ConfigureAwait(false)) return 0;
        string partial = destination + ".part";
        EnsureNoReparsePoints(partial);
        long length = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        return size - (length <= size ? length : 0);
    }
}
