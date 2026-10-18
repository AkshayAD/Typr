using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NAudio.Wave;
using Xunit;
namespace Typr.Infrastructure.Tests;

public sealed class NeMoReplayFactAttribute : FactAttribute
{
    public NeMoReplayFactAttribute()
    {
        var directory = Environment.GetEnvironmentVariable("TYPR_TEST_NEMO_DIR");
        if(string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, NeMoSpeechDownload.ModelFileName)) ||
            !File.Exists(Environment.GetEnvironmentVariable("TYPR_TEST_WAV")))
            Skip = "Set TYPR_TEST_NEMO_DIR and TYPR_TEST_WAV to the verified GPU bundle and pinned synthetic fixture.";
    }
}

[Collection("Native speech integration")]
public sealed class NeMoSpeechTests
{
    [Fact]
    public void IndependentStreamsDrainPartialsAndFinishOnlyOnce()
    {
        var api = new FakeApi();
        using var engine = new NeMoSpeechEngine(api, "model", rightContext: 6);
        using var first = engine.CreateStream();
        first.Accept([.25f, -.5f]);
        api.Results.Enqueue("first partial"); api.Results.Enqueue("first revised");
        Assert.Equal("first revised", first.Decode());
        Assert.Equal("first revised", first.Decode());
        first.InputFinished(); first.InputFinished();
        Assert.Equal(1, api.Finishes);
        Assert.Throws<InvalidOperationException>(() => first.Accept([1]));
        using var second = engine.CreateStream();
        Assert.Equal("", second.Decode());
        Assert.Equal(new float[] { .25f, -.5f }, api.Samples);
        Assert.Equal(6, api.RightContext);
    }

    [Fact]
    public void EngineClosesStreamsBeforeModelAndRejectsUseAfterDispose()
    {
        var api = new FakeApi();
        var engine = new NeMoSpeechEngine(api, "model");
        var first = engine.CreateStream(); var second = engine.CreateStream();
        first.Dispose(); first.Dispose();
        engine.Dispose(); engine.Dispose(); second.Dispose();
        Assert.Equal(new[] { "stream", "stream", "model", "library" }, api.Releases);
        Assert.Throws<ObjectDisposedException>(() => second.Accept([1]));
        Assert.Throws<ObjectDisposedException>(() => second.Decode());
        Assert.Throws<ObjectDisposedException>(() => engine.CreateStream());
    }

    [Fact]
    public async Task DisposeWaitsForAnInFlightNativeDecode()
    {
        var api = new FakeApi();
        using var entered = new ManualResetEventSlim(); using var unblock = new ManualResetEventSlim();
        api.OnNext = () => { entered.Set(); Assert.True(unblock.Wait(TimeSpan.FromSeconds(5))); };
        var engine = new NeMoSpeechEngine(api, "model");
        var stream = engine.CreateStream();
        var decode = Task.Run(stream.Decode);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispose = Task.Run(() => { started.SetResult(); engine.Dispose(); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(dispose.IsCompleted);
        unblock.Set(); await decode.WaitAsync(TimeSpan.FromSeconds(5)); await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "stream", "model", "library" }, api.Releases);
    }

    [Fact]
    public void WarmupUsesAnIsolatedSilenceStreamOnceAndCreationFailureClosesApi()
    {
        var api = new FakeApi();
        using(var engine = new NeMoSpeechEngine(api, "model")) {
            engine.Warmup(); engine.Warmup();
            Assert.Equal(3200, api.Samples.Count);
            Assert.All(api.Samples, sample => Assert.Equal(0, sample));
            Assert.Equal(1, api.Finishes);
            using var next = engine.CreateStream(); Assert.Equal("", next.Decode());
        }
        var broken = new FakeApi { FailCreate = true };
        Assert.Throws<InvalidOperationException>(() => new NeMoSpeechEngine(broken, "model"));
        Assert.Equal(new[] { "library" }, broken.Releases);
    }

    [NeMoReplayFact]
    public void NativeGpuSyntheticReplayCanUnloadAndReopen()
    {
        var directory = Environment.GetEnvironmentVariable("TYPR_TEST_NEMO_DIR")!;
        var fixture = Environment.GetEnvironmentVariable("TYPR_TEST_WAV")!;
        Assert.Equal("71EF05827093D1BE995C395A1EE7798E1A31EE13025382FDE8AD42D8000B366C", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))));
        var native = Environment.GetEnvironmentVariable("TYPR_TEST_NEMO_NATIVE_DIR") ?? Path.Combine(directory, "native");
        using var reader = new WaveFileReader(fixture);
        var bytes = new byte[reader.Length]; reader.ReadExactly(bytes);
        var samples = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2) / 32768f).ToArray();
        for(int reopen = 0; reopen < 2; reopen++) {
            using var engine = new NeMoSpeechEngine(Path.Combine(directory, NeMoSpeechDownload.ModelFileName), native);
            engine.Warmup();
            for(int recording = 0; recording < 2; recording++) {
                using var stream = engine.CreateStream(); bool partial = false;
                for(int offset = 0; offset < samples.Length; offset += 320) {
                    stream.Accept(samples.AsSpan(offset, Math.Min(320, samples.Length - offset)).ToArray());
                    partial |= !string.IsNullOrWhiteSpace(stream.Decode());
                }
                stream.Accept(new float[16000]); stream.InputFinished();
                var actual = Regex.Matches(stream.Decode().ToLowerInvariant(), "[a-z0-9]+").Select(x => x.Value);
                Assert.Equal("hello from typr today is a good day to turn ideas into words".Split(' '), actual);
                Assert.True(partial);
            }
        }
    }

    sealed class FakeApi : INeMoSpeechApi
    {
        public SafeHandle? LibraryHandle => null;
        public List<string> Releases { get; } = [];
        public List<float> Samples { get; } = [];
        public Queue<string> Results { get; } = [];
        public Action? OnNext { get; set; }
        public bool FailCreate { get; init; }
        public int Finishes, RightContext;
        long id = 10;
        public IntPtr CreateRecognizer(string modelPath, bool useGpu, int rightContext)
        {
            if(FailCreate) throw new InvalidOperationException("synthetic initialization failure");
            RightContext = rightContext; return (IntPtr)1;
        }
        public void DestroyRecognizer(IntPtr model) => Releases.Add("model");
        public IntPtr CreateStream(IntPtr model) => (IntPtr)(++id);
        public void CloseStream(IntPtr stream) => Releases.Add("stream");
        public void Push(IntPtr stream, float[] samples) => Samples.AddRange(samples);
        public void Finish(IntPtr stream) => Finishes++;
        public string? Next(IntPtr stream) { OnNext?.Invoke(); return Results.TryDequeue(out var text) ? text : null; }
        public void Dispose() => Releases.Add("library");
    }
}
