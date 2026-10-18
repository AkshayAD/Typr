using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NAudio.Wave;
using Xunit;

namespace Typr.Infrastructure.Tests;

// Large native models run separately from deadline-sensitive fake capture checks.
[CollectionDefinition("Native speech integration", DisableParallelization = true)]
public sealed class NativeSpeechCollection;

[Collection("Native speech integration")]
public sealed class NeMoSessionTests
{
    [NeMoReplayFact]
    public void ProductionGpuPreparationDoesNotOpenInputAndReportsTheEffectiveBackend()
    {
        using var speech = new SpeechSession("unused-cpu-folder", gpuDirectory: Environment.GetEnvironmentVariable("TYPR_TEST_NEMO_DIR"));
        int inputEvents = 0;
        speech.CaptureStateChanged += (_, _) => ++inputEvents;
        speech.Prepare();
        Assert.StartsWith("NVIDIA CUDA", speech.BackendDescription);
        Assert.Contains("560 ms quality", speech.BackendDescription);
        Assert.Null(speech.GpuFallbackReason);
        Assert.Equal(0, inputEvents);
        Assert.Equal(0, speech.CapturedSampleCount);
    }

    [NeMoReplayFact]
    public async Task ImmediateReleaseDrainsGpuAndCaptureStopTailBeforeFinishingTheLastWord()
    {
        string directory = Environment.GetEnvironmentVariable("TYPR_TEST_NEMO_DIR")!;
        byte[] wav = File.ReadAllBytes(Environment.GetEnvironmentVariable("TYPR_TEST_WAV")!);
        Assert.Equal("71EF05827093D1BE995C395A1EE7798E1A31EE13025382FDE8AD42D8000B366C", Convert.ToHexString(SHA256.HashData(wav)));
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var bytes = new byte[reader.Length]; reader.ReadExactly(bytes);
        var samples = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2) / 32768f).ToArray();
        int last = Array.FindLastIndex(samples, sample => Math.Abs(sample) > .0005f);
        samples = samples[..Math.Min(samples.Length, last + 161)];
        // The last half-second is still in the capture driver when release happens.
        // Deliver it only when capture stops, rather than pre-queueing the ending.
        int boundary = (samples.Length - 8000) / 320 * 320;
        using var engine = new NeMoSpeechEngine(Path.Combine(directory, NeMoSpeechDownload.ModelFileName), Path.Combine(directory, NeMoSpeechDownload.NativeDirectoryName));
        engine.Warmup();
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new Input();
        input.OnStop = () => {
            Assert.False(input.Closed);
            input.Emit(samples[boundary..]);
        };
        using var speech = new SpeechSession(() => new DelayedStream(engine.CreateStream(), entered, gate) {
            BeforeFinish = () => Assert.True(input.Closed)
        }, () => input);
        int partials = 0;
        speech.Caption += _ => Interlocked.Increment(ref partials);
        try {
            speech.StartRecording(); await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            input.Emit(samples[..320]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int offset = 320; offset < boundary; offset += 320)
                input.Emit(samples.AsSpan(offset, Math.Min(320, boundary - offset)).ToArray());
            Assert.Equal(0, partials);
            Assert.Equal(boundary, speech.CapturedSampleCount);
            var stopping = speech.StopRecordingAsync();
            await Task.Delay(350);
            Assert.False(stopping.IsCompleted);
            gate.Set();
            string text = await stopping.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("hello from typr today is a good day to turn ideas into words".Split(' '),
                Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]+").Select(match => match.Value));
            Assert.Equal(samples.Length, speech.CapturedSampleCount);
            Assert.True(input.Closed);
        } finally { gate.Set(); }
    }

    sealed class Input : IMicrophoneCapture
    {
        public event Action<float[]>? Samples;
        public event Action<Exception>? Failed { add { } remove { } }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnStop { get; set; }
        public bool Closed { get; private set; }
        public void Start() => Started.TrySetResult();
        public void Emit(float[] samples) => Samples?.Invoke(samples);
        public void Stop() { OnStop?.Invoke(); Closed = true; }
        public void Dispose() => Closed = true;
    }
    sealed class DelayedStream(ISpeechStream stream, TaskCompletionSource entered, ManualResetEventSlim gate) : ISpeechStream
    {
        public Action? BeforeFinish { get; init; }
        public void Accept(float[] samples) => stream.Accept(samples);
        public void InputFinished() { BeforeFinish?.Invoke(); stream.InputFinished(); }
        public string Decode()
        {
            entered.TrySetResult();
            if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic GPU queue gate timed out.");
            return stream.Decode();
        }
        public void Dispose() => stream.Dispose();
    }
}
