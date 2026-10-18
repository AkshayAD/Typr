using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

public sealed class NeMoSpeechDownloadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "typr-nemo-download-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = Encoding.ASCII.GetBytes("verified native fixture");
    private static NeMoSpeechDownload.BundleFile Fixture(string hash = "") => new("bin/test.dll", "native/test.dll", Bytes.Length,
        hash.Length == 0 ? Convert.ToHexString(SHA256.HashData(Bytes)) : hash);

    [Fact] public void ManifestPinsOnlyTheAsrRuntimeAndCarriesUpstreamNotices()
    {
        Assert.Equal(802056425, NeMoSpeechDownload.DownloadBytes);
        Assert.InRange(NeMoSpeechDownload.RequiredDiskBytes, 950_000_000, 1_100_000_000);
        Assert.Equal("model.gguf", NeMoSpeechDownload.ModelFileName);
        Assert.Equal("native", NeMoSpeechDownload.NativeDirectoryName);
        var files = NeMoSpeechDownload.BundleFiles;
        Assert.Equal(27, files.Count);
        Assert.Equal(files.Count, files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(16, files.Count(file => file.RelativePath.StartsWith("native/")));
        Assert.Contains(files, file => file.RelativePath == "native/nemo_speech_asr_c.dll");
        Assert.Contains(files, file => file.RelativePath == "native/ggml-cuda.dll");
        Assert.Contains(files, file => file.RelativePath == "licenses/nemo-speech/THIRD_PARTY_NOTICES.md");
        Assert.DoesNotContain(files, file => file.RelativePath.EndsWith(".exe") || file.RelativePath.Contains("nemo_speech_tts") || file.RelativePath.Contains("nemo_speech_nmt"));
        Assert.All(files, file => { Assert.True(file.Size > 0); Assert.Matches("^[a-f0-9]{64}$", file.Hash); });
        Assert.Throws<NotSupportedException>(() => ((IList<NeMoSpeechDownload.BundleFile>)files).Clear());
    }

    [Fact] public async Task MissingAndPrecancelledSetupHasNoFilesystemOrNetworkSideEffects()
    {
        Assert.False(NeMoSpeechDownload.IsReady(directory));
        Assert.False(await NeMoSpeechDownload.VerifyAsync(directory));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NeMoSpeechDownload.DownloadAsync(directory, null, cancellation.Token));
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task ExtractionWritesOnlyPinnedFilesAndCanRepairACorruptExistingFile()
    {
        Directory.CreateDirectory(Path.Combine(directory, "native"));
        string destination = Path.Combine(directory, "native", "test.dll");
        File.WriteAllText(destination, "previous invalid bytes");
        using var archive = Archive(("bundle/bin/test.dll", Bytes), ("bundle/bin/unrelated.exe", Bytes));
        await NeMoSpeechDownload.ExtractVerifiedAsync(archive, directory, "bundle", [Fixture()], CancellationToken.None);
        Assert.Equal(Bytes, File.ReadAllBytes(destination));
        Assert.Single(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        using var retry = Archive(("bundle/bin/test.dll", Bytes));
        await NeMoSpeechDownload.ExtractVerifiedAsync(retry, directory, "bundle", [Fixture()], CancellationToken.None);
        Assert.Equal(Bytes, File.ReadAllBytes(destination));
    }

    [Fact] public async Task HashFailureKeepsTheOldFileAndNeverCommitsUnverifiedNativeCode()
    {
        Directory.CreateDirectory(Path.Combine(directory, "native"));
        string destination = Path.Combine(directory, "native", "test.dll");
        File.WriteAllText(destination, "previous bytes");
        using var archive = Archive(("bundle/bin/test.dll", Bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => NeMoSpeechDownload.ExtractVerifiedAsync(archive, directory, "bundle", [Fixture(new string('0', 64))], CancellationToken.None));
        Assert.Equal("previous bytes", File.ReadAllText(destination));
        Assert.False(File.Exists(destination + ".extract.part"));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("bundle/../escape.dll")]
    [InlineData("bundle/bin/../../escape.dll")]
    [InlineData("/bundle/bin/escape.dll")]
    [InlineData("bundle/bin\\escape.dll")]
    [InlineData("bundle/bin/escape.dll:stream")]
    [InlineData("bundle/bin/escape.dll.")]
    [InlineData("bundle/bin/escape.dll ")]
    [InlineData("other/bin/escape.dll")]
    public async Task UnsafeArchiveEntriesAreRejectedBeforeAnyFileIsWritten(string name)
    {
        using var archive = Archive(("bundle/bin/test.dll", Bytes), (name, Bytes));
        await Assert.ThrowsAsync<InvalidDataException>(() => NeMoSpeechDownload.ExtractVerifiedAsync(archive, directory, "bundle", [Fixture()], CancellationToken.None));
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task SymlinkAndCaseAliasedEntriesAreRejectedBeforeExtraction()
    {
        using (var archive = Archive(("bundle/bin/test.dll", Bytes), ("bundle/bin/TEST.dll", Bytes)))
            await Assert.ThrowsAsync<InvalidDataException>(() => NeMoSpeechDownload.ExtractVerifiedAsync(archive, directory, "bundle", [Fixture()], CancellationToken.None));
        using (var archive = Archive(("bundle/bin/test.dll", Bytes)))
        {
            archive.Entries[0].ExternalAttributes = unchecked((int)0xA1FF0000);
            await Assert.ThrowsAsync<InvalidDataException>(() => NeMoSpeechDownload.ExtractVerifiedAsync(archive, directory, "bundle", [Fixture()], CancellationToken.None));
        }
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task MissingOrWrongSizedSelectedEntriesCannotModifyExistingFiles()
    {
        Directory.CreateDirectory(Path.Combine(directory, "native"));
        string destination = Path.Combine(directory, "native", "test.dll");
        File.WriteAllText(destination, "keep existing file");
        using (var archive = Archive(("bundle/bin/unrelated.dll", Bytes)))
            await Assert.ThrowsAsync<InvalidDataException>(() => NeMoSpeechDownload.ExtractVerifiedAsync(archive, directory, "bundle", [Fixture()], CancellationToken.None));
        using (var archive = Archive(("bundle/bin/test.dll", Bytes[..^1])))
            await Assert.ThrowsAsync<InvalidDataException>(() => NeMoSpeechDownload.ExtractVerifiedAsync(archive, directory, "bundle", [Fixture()], CancellationToken.None));
        Assert.Equal("keep existing file", File.ReadAllText(destination));
    }

    [Fact] public async Task AnExistingDirectoryJunctionCannotRedirectNativeExtractionOrDownloads()
    {
        Directory.CreateDirectory(Path.Combine(directory, "outside"));
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = $"/d /c mklink /J \"{Path.Combine(directory, "native")}\" \"{Path.Combine(directory, "outside")}\"";
        using (var process = Process.Start(start)!)
        {
            Assert.True(process.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
        }
        using var archive = Archive(("bundle/bin/test.dll", Bytes));
        await Assert.ThrowsAsync<IOException>(() => NeMoSpeechDownload.ExtractVerifiedAsync(archive, directory, "bundle", [Fixture()], CancellationToken.None));
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Must fail before network access.")));
        await Assert.ThrowsAsync<IOException>(() => NeMoSpeechDownload.DownloadFileAsync(client, new Uri("https://example.invalid/runtime"),
            Path.Combine(directory, "native", "test.dll"), Bytes.Length, Fixture().Hash, _ => { }, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "outside")));
    }

    [Fact] public async Task DownloadResumesAPartialAndChecksItsHashBeforeReplacingTheDestination()
    {
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "runtime.zip");
        File.WriteAllBytes(destination + ".part", Bytes[..5]);
        using var client = new HttpClient(new Handler(request => {
            Assert.Equal(5, Assert.Single(request.Headers.Range!.Ranges).From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Bytes[5..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, Bytes.Length - 1, Bytes.Length);
            return response;
        }));
        await NeMoSpeechDownload.DownloadFileAsync(client, new Uri("https://example.invalid/runtime"), destination, Bytes.Length, Fixture().Hash, _ => { }, CancellationToken.None);
        Assert.Equal(Bytes, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    private static ZipArchive Archive(params (string Name, byte[] Bytes)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in entries)
            {
                using var output = zip.CreateEntry(entry.Name).Open();
                output.Write(entry.Bytes);
            }
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Update);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }

    public void Dispose()
    {
        string native = Path.Combine(directory, "native");
        if (Directory.Exists(native) && (File.GetAttributes(native) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(native); // Remove the test junction itself before recursively cleaning its parent.
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
