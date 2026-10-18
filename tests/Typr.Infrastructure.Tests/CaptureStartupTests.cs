using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

/// <summary>Startup boundaries with synthetic sources; never opens audio hardware.</summary>
public sealed class CaptureStartupTests
{
    static readonly TimeSpan Timeout=TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseOrCancelDuringNativeConstructionNeverStartsInput(bool cancel)
    {
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source=new FakeSource { OnOpen=() => {
            entered.TrySetResult();
            if(!release.Wait(Timeout)) throw new TimeoutException();
        } };
        var stream=new FakeStream();
        var states=new ConcurrentQueue<bool>();
        var ready=new ConcurrentQueue<int>();
        using var session=new SpeechSession(() => stream,() => new SharedMicrophoneCapture(source.Open));
        session.CaptureStateChanged+=(_,active) => states.Enqueue(active);
        session.InputReady+=ready.Enqueue;
        Task<string>? stopping=null;
        try {
            session.StartRecording();
            await entered.Task.WaitAsync(Timeout);
            if(cancel) session.CancelRecording();
            else stopping=session.StopRecordingAsync();
        }
        finally { release.Set(); }
        await stream.Disposed.Task.WaitAsync(Timeout);
        if(stopping!=null) Assert.Equal("",await stopping.WaitAsync(Timeout));
        Assert.Equal(0,source.Starts);
        Assert.Equal(0,source.Stops);
        Assert.Equal(1,source.Disposes);
        Assert.Empty(states);
        Assert.Empty(ready);
        Assert.Empty(stream.Samples);
        Assert.False(stream.Finished);
    }

    [Fact]
    public async Task FirstPcmRaisesReadinessOnceBeforeInferenceAndOldCallbacksCannotReadyNextInput()
    {
        var first=new FakeCapture(); var next=new FakeCapture();
        var firstStream=new FakeStream(); var nextStream=new FakeStream();
        var inputs=new Queue<FakeCapture>([first,next]);
        var streams=new Queue<FakeStream>([firstStream,nextStream]);
        var ready=new ConcurrentQueue<int>();
        using var active=new SemaphoreSlim(0);
        using var session=new SpeechSession(() => streams.Dequeue(),() => inputs.Dequeue());
        session.CaptureStateChanged+=(_,started) => { if(started) active.Release(); };
        session.InputReady+=ready.Enqueue;

        int previous=session.StartRecording();
        Assert.True(await active.WaitAsync(Timeout));
        Assert.Empty(ready);
        firstStream.OnAccept=() => Assert.Equal(previous,Assert.Single(ready));
        first.Emit([]);
        Assert.Empty(ready);
        first.Emit([0,.25f,.5f]); // A valid silent sample also establishes readiness.
        first.Emit([.75f]);
        Assert.Equal(previous,Assert.Single(ready));
        Assert.Equal("synthetic result",await session.StopRecordingAsync().WaitAsync(Timeout));
        Assert.Equal(new float[] {0,.25f,.5f,.75f},firstStream.Samples.Take(4));
        Assert.Equal(16004,firstStream.Samples.Count);

        int current=session.StartRecording();
        Assert.True(await active.WaitAsync(Timeout));
        first.Emit([.99f]);
        Assert.Equal(previous,Assert.Single(ready));
        nextStream.OnAccept=() => Assert.Equal(new[] {previous,current},ready.ToArray());
        next.Emit([0]);
        Assert.Equal(new[] {previous,current},ready.ToArray());
        Assert.Equal("synthetic result",await session.StopRecordingAsync().WaitAsync(Timeout));
        Assert.Equal(16001,nextStream.Samples.Count);
    }

    [Fact]
    public async Task OverflowBeforeFirstAdmittedPcmDoesNotReportReadiness()
    {
        var input=new FakeCapture(); var stream=new FakeStream();
        var ready=new ConcurrentQueue<int>();
        using var session=new SpeechSession(() => stream,() => input);
        session.InputReady+=ready.Enqueue;
        session.StartRecording();
        await input.Started.Task.WaitAsync(Timeout);
        input.Emit(new float[SpeechSession.MaximumBufferedAudioSamples+1]);
        await input.Disposed.Task.WaitAsync(Timeout);
        Assert.Empty(ready);
        Assert.Empty(stream.Samples);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopRecordingAsync().WaitAsync(Timeout));
    }

    [Fact]
    public async Task FailedNativeStartNeverPublishesAnActiveCaptureOrReadiness()
    {
        var source=new FakeSource { OnStart=() => throw new InvalidOperationException("synthetic native start failure") };
        var stream=new FakeStream();
        var states=new ConcurrentQueue<bool>();
        var ready=new ConcurrentQueue<int>();
        var error=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session=new SpeechSession(() => stream,() => new SharedMicrophoneCapture(source.Open));
        session.CaptureStateChanged+=(_,active) => states.Enqueue(active);
        session.InputReady+=ready.Enqueue;
        session.Error+=message => error.TrySetResult(message);
        session.StartRecording();
        Assert.Contains("native start failure",await error.Task.WaitAsync(Timeout));
        await stream.Disposed.Task.WaitAsync(Timeout);
        Assert.Equal(1,source.Starts);
        Assert.Equal(0,source.Stops);
        Assert.Equal(1,source.Disposes);
        Assert.Empty(states);
        Assert.Empty(ready);
    }

    sealed class FakeSource : INativeCaptureSource
    {
        readonly WaveFormat format=WaveFormat.CreateIeeeFloatWaveFormat(16000,1);
        int ownerThread;
        public Action? OnOpen {get;init;}
        public Action? OnStart {get;init;}
        public int Starts,Stops,Disposes;
        public INativeCaptureSource Open() { ownerThread=Environment.CurrentManagedThreadId; OnOpen?.Invoke(); return this; }
        void CheckThread() => Assert.Equal(ownerThread,Environment.CurrentManagedThreadId);
        public WaveFormat WaveFormat { get { CheckThread(); return format; } }
        public int PollMilliseconds { get { CheckThread(); return 60000; } }
        public void Start() { CheckThread(); Starts++; OnStart?.Invoke(); }
        public void Stop() { CheckThread(); Stops++; }
        public int GetNextPacketSize() { CheckThread(); return 0; }
        public IntPtr GetBuffer(out int frames,out AudioClientBufferFlags flags) => throw new InvalidOperationException("No packets were queued.");
        public void ReleaseBuffer(int frames) => throw new InvalidOperationException("No packets were queued.");
        public void Dispose() { CheckThread(); Disposes++; }
    }

    sealed class FakeCapture : IMicrophoneCapture
    {
        public event Action<float[]>? Samples;
        public event Action<Exception>? Failed { add {} remove {} }
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Start() => Started.TrySetResult();
        public void Emit(float[] samples) => Samples?.Invoke(samples);
        public void Stop() { }
        public void Dispose() => Disposed.TrySetResult();
    }

    sealed class FakeStream : ISpeechStream
    {
        public List<float> Samples {get;}=[];
        public TaskCompletionSource Disposed {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnAccept {get;set;}
        public bool Finished {get;private set;}
        public void Accept(float[] samples) { OnAccept?.Invoke(); Samples.AddRange(samples); }
        public void InputFinished() => Finished=true;
        public string Decode() => "synthetic result";
        public void Dispose() => Disposed.TrySetResult();
    }
}
