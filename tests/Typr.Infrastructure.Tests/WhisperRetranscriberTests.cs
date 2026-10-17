using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Typr.Infrastructure;
using NAudio.Wave;
using Xunit;

namespace Typr.Infrastructure.Tests;

public sealed class WhisperRetranscriberTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "typr-whisper-tests-" + Guid.NewGuid().ToString("N"));
    private string AudioPath => Path.Combine(directory, "audio.wav");

    [Fact] public void CatalogIsBoundedMultilingualAndHasExactPinnedDownloadSizes()
    {
        Assert.Equal(new[] { "whisper-small", "whisper-large-v3" }, WhisperRetranscriber.Models.Select(model => model.Id));
        Assert.Equal(375485327, WhisperRetranscriber.Models[0].DownloadBytes);
        Assert.Equal(1775753918, WhisperRetranscriber.Models[1].DownloadBytes);
        Assert.All(WhisperRetranscriber.Models, model => Assert.Contains("multilingual", model.Name));
        Assert.Throws<NotSupportedException>(() => ((IList<RetranscriptionModel>)WhisperRetranscriber.Models).Clear());
    }

    [Fact] public async Task UnknownIdsCannotCreateDirectoriesOrStartNetworkOrInference()
    {
        var service = new WhisperRetranscriber(directory);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.IsReady("../unexpected"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.DownloadAsync("unknown"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.TranscribeAsync("missing.wav", "unknown", 2));
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public async Task MissingModelNeverReturnsASuccessfulTranscript()
    {
        WriteWave(new WaveFormat(16000, 16, 1), [1, 2, 3]);
        var service = new WhisperRetranscriber(directory);
        Assert.False(service.IsReady("whisper-small"));
        Assert.False(service.IsReady("whisper-large-v3"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.TranscribeAsync(AudioPath, "whisper-small", 2));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(8000, 16, 1)]
    [InlineData(16000, 16, 2)]
    [InlineData(16000, 8, 1)]
    public async Task UnsupportedAudioFailsBeforeLoadingAModel(int rate, int bits, int channels)
    {
        WriteWave(new WaveFormat(rate, bits, channels), [1, 2, 3]);
        var service = new WhisperRetranscriber(directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.TranscribeAsync(AudioPath, "whisper-small", 2));
    }

    [Fact] public async Task EmptyAudioAndMissingAudioAreNotSuccessful()
    {
        Directory.CreateDirectory(directory);
        var service = new WhisperRetranscriber(directory);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.TranscribeAsync(AudioPath, "whisper-small", 2));
        WriteWave(new WaveFormat(16000, 16, 1), []);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.TranscribeAsync(AudioPath, "whisper-small", 2));
    }

    [Fact] public async Task PrecancelledOperationsHaveNoDownloadOrTranscriptionSideEffects()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var service = new WhisperRetranscriber(directory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync("whisper-small", null, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranscribeAsync(AudioPath, "whisper-small", 2, cancel.Token));
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public void AudioChunkingPreservesSamplesAndNeverExceedsTwentyFiveSeconds()
    {
        var samples = Enumerable.Range(0, WhisperRetranscriber.ChunkSamples + 7).Select(index => (short)(index % 30000)).ToArray();
        WriteWave(new WaveFormat(16000, 16, 1), samples);
        using var audio = WhisperRetranscriber.OpenAudio(AudioPath);
        var chunks = WhisperRetranscriber.ReadChunks(audio, CancellationToken.None).ToArray();
        Assert.Equal(new[] { WhisperRetranscriber.ChunkSamples, 7 }, chunks.Select(chunk => chunk.Length));
        Assert.Equal(samples.Select(sample => sample / 32768f), chunks.SelectMany(chunk => chunk));
    }

    [Fact] public async Task FileVerificationChecksSizeAndShaRatherThanPresenceAlone()
    {
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes("synthetic model fixture");
        File.WriteAllBytes(AudioPath, bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.True(await WhisperRetranscriber.VerifyFileAsync(AudioPath, bytes.Length, hash, CancellationToken.None));
        Assert.False(await WhisperRetranscriber.VerifyFileAsync(AudioPath, bytes.Length + 1, hash, CancellationToken.None));
        Assert.False(await WhisperRetranscriber.VerifyFileAsync(AudioPath, bytes.Length, new string('0', 64), CancellationToken.None));
    }

    [Fact] public async Task ResumeRequestsTheRemainingBytesAndRenamesOnlyAfterVerification()
    {
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.ASCII.GetBytes("model bytes");
        File.WriteAllBytes(AudioPath + ".part", bytes[..4]);
        using var client = new HttpClient(new ResponseHandler(request => {
            Assert.Equal(4, Assert.Single(request.Headers.Range!.Ranges).From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[4..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4, bytes.Length - 1, bytes.Length);
            return response;
        }));
        await DownloadFixture(client, bytes);
        Assert.Equal(bytes, File.ReadAllBytes(AudioPath));
        Assert.False(File.Exists(AudioPath + ".part"));
    }

    [Fact] public async Task IgnoredRangeRestartsWithoutDuplicatingThePartialPrefix()
    {
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.ASCII.GetBytes("model bytes");
        File.WriteAllBytes(AudioPath + ".part", bytes[..4]);
        using var client = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        await DownloadFixture(client, bytes);
        Assert.Equal(bytes, File.ReadAllBytes(AudioPath));
    }

    [Fact] public async Task HashMismatchCannotReplaceAnExistingModel()
    {
        Directory.CreateDirectory(directory);
        byte[] expected = Encoding.ASCII.GetBytes("model bytes"), corrupt = Encoding.ASCII.GetBytes("wrong bytes");
        File.WriteAllText(AudioPath, "preserve existing file");
        using var client = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt) }));
        await Assert.ThrowsAsync<IOException>(() => DownloadFixture(client, expected));
        Assert.Equal("preserve existing file", File.ReadAllText(AudioPath));
        Assert.False(File.Exists(AudioPath + ".part"));
    }

    [Fact] public async Task TruncatedTransferLeavesAResumablePartialWithoutClaimingCompletion()
    {
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.ASCII.GetBytes("model bytes");
        using var client = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[..4]) }));
        await Assert.ThrowsAsync<IOException>(() => DownloadFixture(client, bytes));
        Assert.False(File.Exists(AudioPath));
        Assert.Equal(bytes[..4], File.ReadAllBytes(AudioPath + ".part"));
    }

    [Fact] public async Task MismatchedResumeResponsePreservesTheExistingPartial()
    {
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.ASCII.GetBytes("model bytes");
        File.WriteAllBytes(AudioPath + ".part", bytes[..4]);
        using var client = new HttpClient(new ResponseHandler(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[3..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, bytes.Length - 1, bytes.Length);
            return response;
        }));
        await Assert.ThrowsAsync<IOException>(() => DownloadFixture(client, bytes));
        Assert.False(File.Exists(AudioPath));
        Assert.Equal(bytes[..4], File.ReadAllBytes(AudioPath + ".part"));
    }

    [Fact] public async Task CancellationAfterReceivingBytesNeverCommitsAnUnverifiedFile()
    {
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.ASCII.GetBytes("model bytes");
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WhisperRetranscriber.DownloadFileAsync(client,
            new Uri("https://example.invalid/synthetic-model"), AudioPath, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            received => { if (received > 0) cancel.Cancel(); }, cancel.Token));
        Assert.False(File.Exists(AudioPath));
        Assert.True(File.Exists(AudioPath + ".part"));
    }

    private Task DownloadFixture(HttpClient client, byte[] bytes) => WhisperRetranscriber.DownloadFileAsync(client,
        new Uri("https://example.invalid/synthetic-model"), AudioPath, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), _ => { }, CancellationToken.None);

    private void WriteWave(WaveFormat format, short[] samples)
    {
        Directory.CreateDirectory(directory);
        using var writer = new WaveFileWriter(AudioPath, format);
        foreach (short sample in samples) writer.Write(BitConverter.GetBytes(sample), 0, 2);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
