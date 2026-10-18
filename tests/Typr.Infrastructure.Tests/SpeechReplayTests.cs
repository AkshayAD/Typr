using System.Collections.Concurrent;
using Typr.Infrastructure;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;
using SherpaOnnx;

namespace Typr.Infrastructure.Tests;

public sealed class ModelReplayFactAttribute : FactAttribute
{
    public ModelReplayFactAttribute()
    {
        if (!Directory.Exists(Environment.GetEnvironmentVariable("TYPR_TEST_MODEL")) ||
            !File.Exists(Environment.GetEnvironmentVariable("TYPR_TEST_WAV")))
            Skip = "Set TYPR_TEST_MODEL and TYPR_TEST_WAV to verified model and synthetic PCM16 mono 16kHz audio.";
    }
}

[Collection("Native speech integration")]
public sealed class SpeechReplayTests(ITestOutputHelper output)
{
    [ModelReplayFact]
    public void UnavailableGpuFallsBackToPreparedCpuWithoutOpeningInput()
    {
        using var speech = new SpeechSession(Environment.GetEnvironmentVariable("TYPR_TEST_MODEL")!,
            gpuDirectory: Path.Combine(Path.GetTempPath(), "typr-missing-gpu-" + Guid.NewGuid().ToString("N")));
        int inputEvents = 0;
        speech.CaptureStateChanged += (_, _) => ++inputEvents;
        speech.Prepare();
        Assert.StartsWith("CPU", speech.BackendDescription);
        Assert.Contains("GPU speech could not start", speech.GpuFallbackReason);
        Assert.Equal(0, inputEvents);
        Assert.Equal(0, speech.CapturedSampleCount);
    }

    [ModelReplayFact]
    public async Task RealModelRecognizesPacedAudioCancelsFinalizationAndStartsAFreshGeneration()
    {
        string? previous = Environment.GetEnvironmentVariable("TYPR_REPLAY_WAV");
        string model = Environment.GetEnvironmentVariable("TYPR_TEST_MODEL")!;
        string wave = Environment.GetEnvironmentVariable("TYPR_TEST_WAV")!;
        Environment.SetEnvironmentVariable("TYPR_REPLAY_WAV", wave);
        try
        {
            using var reader = new WaveFileReader(wave);
            using var speech = new SpeechSession(model, savedAudioLimit: 0);
            var captions = new ConcurrentQueue<int>();
            var errors = new ConcurrentQueue<string>();
            speech.CaptionForRecording += (generation, _) => captions.Enqueue(generation);
            speech.Error += errors.Enqueue;
            speech.Prepare(); // Uses the explicit synthetic replay path, never the microphone.
            int first = speech.StartRecording();
            await Task.Delay(reader.TotalTime + TimeSpan.FromMilliseconds(800));
            var stopClock=Stopwatch.StartNew();
            string result = await speech.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(30));
            output.WriteLine($"Synthetic replay stop: {stopClock.Elapsed.TotalMilliseconds:F1}ms; final characters={result.Length}.");
            output.WriteLine($"Synthetic replay captured frames={speech.CapturedSampleCount}; fixture frames={reader.Length/2}.");
            Assert.Equal(reader.Length/2,speech.CapturedSampleCount);
            Assert.Equal("Hello from TYPR today is a good day to turn ideas into words", result);
            Assert.Contains(first, captions);

            int cancelled = speech.StartRecording();
            await Task.Delay(700);
            using var cancellation = new CancellationTokenSource();
            var finalization = speech.StopRecordingAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalization);

            int next = speech.StartRecording();
            Assert.NotEqual(cancelled, next);
            Assert.NotEqual(first, next);
            await Task.Delay(reader.TotalTime + TimeSpan.FromMilliseconds(800));
            string recovered = await speech.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(reader.Length/2,speech.CapturedSampleCount);
            Assert.Equal(result, recovered);
            Assert.Contains(next, captions);
            Assert.Empty(errors);
        }
        finally { Environment.SetEnvironmentVariable("TYPR_REPLAY_WAV", previous); }
    }

    [ModelReplayFact]
    public async Task ReleaseBeforeAnyPartialDrainsTheLastWordWithoutFixtureTrailingSilence()
    {
        string model=Environment.GetEnvironmentVariable("TYPR_TEST_MODEL")!;
        using var reader=new WaveFileReader(Environment.GetEnvironmentVariable("TYPR_TEST_WAV")!);
        var bytes=new byte[reader.Length]; reader.ReadExactly(bytes);
        var samples=Enumerable.Range(0,bytes.Length/2).Select(i=>BitConverter.ToInt16(bytes,i*2)/32768f).ToArray();
        // The old fixture includes almost 800ms of trailing quiet. Keep just
        // 10ms after its final audible sample so that silence cannot mask loss.
        int last=Array.FindLastIndex(samples,sample=>Math.Abs(sample)>.0005f);
        Assert.True(last>=0);
        samples=samples[..Math.Min(samples.Length,last+161)];
        var config=new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate=16000; config.FeatConfig.FeatureDim=80;
        config.ModelConfig.Transducer.Encoder=Path.Combine(model,"encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder=Path.Combine(model,"decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner=Path.Combine(model,"joiner.int8.onnx");
        config.ModelConfig.Tokens=Path.Combine(model,"tokens.txt");
        config.ModelConfig.NumThreads=2; config.ModelConfig.Provider="cpu";
        config.DecodingMethod="greedy_search"; config.EnableEndpoint=0;
        using var recognizer=new OnlineRecognizer(config);
        using var decodeGate=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input=new BoundaryCapture();
        using var session=new SpeechSession(()=>new BlockedNativeStream(recognizer,entered,decodeGate),()=>input);
        int partials=0; session.Caption+=text=>{ if(!string.IsNullOrWhiteSpace(text)) Interlocked.Increment(ref partials); };
        try {
            session.StartRecording(); await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            input.Emit(samples[..8960]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for(int offset=8960;offset<samples.Length;offset+=320)
                input.Emit(samples.AsSpan(offset,Math.Min(320,samples.Length-offset)).ToArray());
            Assert.Equal(0,Volatile.Read(ref partials));
            var stopping=session.StopRecordingAsync();
            await Task.Delay(350); // The stop is queued while native decoding lags.
            Assert.False(stopping.IsCompleted);
            decodeGate.Set();
            string result=await stopping.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(samples.Length,session.CapturedSampleCount);
            Assert.True(input.Disposed);
            Assert.Equal("Hello from TYPR today is a good day to turn ideas into words",result);
            output.WriteLine($"Immediate release: {samples.Length} admitted frames; complete final words retained with no pre-release partial.");
        } finally { decodeGate.Set(); }
    }

    sealed class BoundaryCapture:IMicrophoneCapture
    {
        public event Action<float[]>? Samples;
        public event Action<Exception>? Failed { add {} remove {} }
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed {get;private set;}
        public void Start()=>Started.TrySetResult();
        public void Emit(float[] samples)=>Samples?.Invoke(samples);
        public void Stop() {}
        public void Dispose()=>Disposed=true;
    }
    sealed class BlockedNativeStream(OnlineRecognizer recognizer,TaskCompletionSource entered,ManualResetEventSlim gate):ISpeechStream
    {
        readonly SherpaSpeechStream stream=new(recognizer);
        public void Accept(float[] samples)=>stream.Accept(samples);
        public void InputFinished()=>stream.InputFinished();
        public string Decode() {
            entered.TrySetResult();
            if(!gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic decode gate timed out.");
            return stream.Decode();
        }
        public void Dispose()=>stream.Dispose();
    }
}
