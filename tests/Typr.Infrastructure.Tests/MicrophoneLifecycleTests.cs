using System.Collections.Concurrent;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

/// <summary>Exercises the real worker/state machine with no model, COM or audio hardware.</summary>
public sealed class MicrophoneLifecycleTests
{
    static readonly TimeSpan Timeout=TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PrepareAndIdleNeverCreateInputAndStopClosesBeforeFinalization()
    {
        int opens=0;
        var input=new FakeCapture();
        var stream=new FakeStream();
        stream.OnFinish=() => Assert.True(input.Disposed.Task.IsCompletedSuccessfully);
        using var session=new SpeechSession(() => stream,() => { Interlocked.Increment(ref opens); return input; });
        session.Prepare(); session.Prepare();
        Assert.Equal(0,opens);
        session.StartRecording();
        await input.Started.Task.WaitAsync(Timeout);
        Assert.Equal(1,opens);
        input.Emit([.25f,.5f]);
        input.OnStop=() => input.Emit([.75f]); // Driver's final block belongs to this recording.
        var stopping=session.StopRecordingAsync();
        input.Emit([.6f]); // Already-started capture retains audio during the release tail.
        Assert.Equal("synthetic result",await stopping.WaitAsync(Timeout));
        Assert.Equal(1,input.Starts);
        Assert.Equal(1,input.Stops);
        Assert.Equal(1,input.Disposes);
        Assert.Equal(new[] {.25f,.5f,.6f,.75f},stream.Samples.Where(x => x!=0).ToArray());
        Assert.Null(session.LastAudioFileName);
        input.Emit([.99f]);
        input.Fail(new InvalidOperationException("late callback"));
        Assert.Equal(1,opens);
    }

    [Fact]
    public async Task CancelClosesInputAndOldCallbacksCannotReachTheNextRecording()
    {
        var first=new FakeCapture(); var second=new FakeCapture();
        var firstStream=new FakeStream(); var secondStream=new FakeStream();
        var inputs=new Queue<FakeCapture>([first,second]);
        var streams=new Queue<FakeStream>([firstStream,secondStream]);
        var errors=new ConcurrentQueue<string>(); var levels=new ConcurrentQueue<float>();
        using var session=new SpeechSession(() => streams.Dequeue(),() => inputs.Dequeue());
        session.Error += errors.Enqueue; session.Level += levels.Enqueue;
        int previous=session.StartRecording();
        await first.Started.Task.WaitAsync(Timeout);
        session.CancelRecording();
        await first.Disposed.Task.WaitAsync(Timeout);
        Assert.NotEqual(previous,session.StartRecording());
        await second.Started.Task.WaitAsync(Timeout);
        first.Emit(Enumerable.Repeat(.9f,8960).ToArray());
        first.Fail(new InvalidOperationException("stale device"));
        second.Emit([.2f]);
        Assert.Equal("synthetic result",await session.StopRecordingAsync().WaitAsync(Timeout));
        Assert.Equal(new[] {.2f},secondStream.Samples.Where(x => x!=0).ToArray());
        Assert.Empty(firstStream.Samples);
        Assert.Single(levels);
        Assert.Empty(errors);
        Assert.Equal(1,first.Disposes); Assert.Equal(1,second.Disposes);
    }

    [Fact]
    public async Task CancellationWhileInputCreationIsPendingNeverStartsLateInput()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();
        var input=new FakeCapture();
        using var session=new SpeechSession(() => new FakeStream(),() => {
            entered.TrySetResult();
            if(!release.Wait(Timeout)) throw new TimeoutException();
            return input;
        });
        try {
            session.StartRecording();
            await entered.Task.WaitAsync(Timeout);
            session.CancelRecording();
        } finally { release.Set(); }
        await input.Disposed.Task.WaitAsync(Timeout);
        Assert.Equal(0,input.Starts);
        Assert.Equal(1,input.Disposes);
    }

    [Fact]
    public async Task CancelledQueuedStartNeverConstructsInput()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();
        int opens=0;
        using var session=new SpeechSession(() => {
            entered.TrySetResult();
            if(!release.Wait(Timeout)) throw new TimeoutException();
            return new FakeStream { OnDispose=() => disposed.TrySetResult() };
        },() => { Interlocked.Increment(ref opens); return new FakeCapture(); });
        try {
            session.StartRecording();
            await entered.Task.WaitAsync(Timeout);
            session.CancelRecording();
        } finally { release.Set(); }
        await disposed.Task.WaitAsync(Timeout);
        Assert.Equal(0,opens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopDuringPendingFactoryPreventsLateInputStartAndAllowsNextRecording(bool streamFactory)
    {
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstInput=new FakeCapture(); var nextInput=new FakeCapture();
        var firstStream=new FakeStream(); var nextStream=new FakeStream();
        var errors=new ConcurrentQueue<string>();
        int streams=0,inputs=0;
        void WaitForRelease() {
            entered.TrySetResult();
            if(!release.Wait(Timeout)) throw new TimeoutException();
        }
        using var session=new SpeechSession(() => {
            if(Interlocked.Increment(ref streams)>1) return nextStream;
            if(streamFactory) WaitForRelease();
            return firstStream;
        },() => {
            Interlocked.Increment(ref inputs);
            if(streamFactory) return nextInput;
            if(inputs>1) return nextInput;
            WaitForRelease();
            return firstInput;
        });
        session.Error += errors.Enqueue;
        session.StartRecording();
        await entered.Task.WaitAsync(Timeout);
        Task<string> stopping;
        try {
            stopping=session.StopRecordingAsync(); // Revoke before the worker is released.
        } finally { release.Set(); }
        Assert.Equal("",await stopping.WaitAsync(Timeout));
        Assert.False(firstStream.Finished);
        Assert.Equal(0,firstInput.Starts);
        Assert.Equal(streamFactory ? 0 : 1,inputs);
        Assert.Equal(streamFactory ? 0 : 1,firstInput.Disposes);
        Assert.Empty(firstStream.Samples);
        Assert.Equal(0,firstStream.Decodes);

        session.StartRecording();
        await nextInput.Started.Task.WaitAsync(Timeout);
        firstInput.Emit([.9f]);
        nextInput.Emit([.2f]);
        Assert.Equal("synthetic result",await session.StopRecordingAsync().WaitAsync(Timeout));
        Assert.Equal(new[] {.2f},nextStream.Samples.Where(x => x!=0).ToArray());
        Assert.Equal(1,nextInput.Starts); Assert.Equal(1,nextInput.Disposes);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task CaptureLifetimeEventsKeepMicrophoneActiveThroughTailAndCloseBeforeFinalization()
    {
        var input=new FakeCapture(); var stream=new FakeStream();
        var states=new ConcurrentQueue<(int Generation,bool Active)>();
        var active=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session=new SpeechSession(() => stream,() => input);
        session.CaptureStateChanged += (generation,isActive) => {
            if(!isActive) Assert.True(input.Disposed.Task.IsCompletedSuccessfully);
            states.Enqueue((generation,isActive));
            if(isActive) active.TrySetResult();
        };
        stream.OnFinish=() => Assert.False(states.Last().Active);
        int epoch=session.StartRecording();
        await active.Task.WaitAsync(Timeout);
        var stopping=session.StopRecordingAsync();
        Assert.Equal(new[] {(epoch,true)},states.ToArray());
        input.Emit([.3f]);
        Assert.Equal("synthetic result",await stopping.WaitAsync(Timeout));
        Assert.Equal(new[] {(epoch,true),(epoch,false)},states.ToArray());
        Assert.Contains(.3f,stream.Samples);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CloseErrorsStillPublishInactiveAfterDisposal(bool stopFailure)
    {
        var input=new FakeCapture {
            OnStop=stopFailure ? () => throw new InvalidOperationException("synthetic stop failure") : null,
            OnDispose=stopFailure ? null : () => throw new InvalidOperationException("synthetic dispose failure")
        };
        var states=new ConcurrentQueue<bool>();
        var active=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session=new SpeechSession(() => new FakeStream(),() => input);
        session.CaptureStateChanged += (_,isActive) => {
            if(!isActive) Assert.True(input.Disposed.Task.IsCompletedSuccessfully);
            states.Enqueue(isActive);
            if(isActive) active.TrySetResult();
        };
        session.StartRecording();
        await active.Task.WaitAsync(Timeout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopRecordingAsync());
        Assert.Equal(new[] {true,false},states.ToArray());
        Assert.Equal(1,input.Disposes);
    }

    [Fact]
    public async Task CancellationWhileStartIsPendingClosesAsSoonAsStartReturns()
    {
        using var release=new ManualResetEventSlim();
        var input=new FakeCapture { OnStart=() => { if(!release.Wait(Timeout)) throw new TimeoutException(); } };
        using var session=new SpeechSession(() => new FakeStream(),() => input);
        try {
            session.StartRecording();
            await input.Started.Task.WaitAsync(Timeout);
            session.CancelRecording();
        } finally { release.Set(); }
        await input.Disposed.Task.WaitAsync(Timeout);
        Assert.Equal(1,input.Starts); Assert.Equal(1,input.Stops); Assert.Equal(1,input.Disposes);
    }

    [Fact]
    public async Task CaptureFailureClosesInputAndPreventsSuccessfulFinalization()
    {
        var input=new FakeCapture(); var stream=new FakeStream();
        var error=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session=new SpeechSession(() => stream,() => input);
        session.Error += message => error.TrySetResult(message);
        session.StartRecording();
        await input.Started.Task.WaitAsync(Timeout);
        input.Fail(new InvalidOperationException("synthetic device failure"));
        await input.Disposed.Task.WaitAsync(Timeout);
        Assert.Equal("synthetic device failure",await error.Task.WaitAsync(Timeout));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopRecordingAsync());
        Assert.Null(session.LastAudioFileName);
        Assert.False(stream.Finished);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupAndInferenceFailuresDisposeInput(bool startup)
    {
        var input=new FakeCapture { OnStart=startup ? () => throw new InvalidOperationException("synthetic startup failure") : null };
        var stream=new FakeStream { OnAccept=startup ? null : () => throw new InvalidOperationException("synthetic inference failure") };
        var error=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session=new SpeechSession(() => stream,() => input);
        session.Error += message => error.TrySetResult(message);
        session.StartRecording();
        await input.Started.Task.WaitAsync(Timeout);
        if(!startup) input.Emit(new float[8960]);
        await input.Disposed.Task.WaitAsync(Timeout);
        Assert.Contains(startup ? "startup" : "inference",await error.Task.WaitAsync(Timeout));
        Assert.Equal(1,input.Disposes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopRecordingAsync());
        Assert.Null(session.LastAudioFileName);
    }

    [Fact]
    public async Task StopCancellationClosesInputWithoutFinishingStream()
    {
        var input=new FakeCapture(); var stream=new FakeStream();
        using var session=new SpeechSession(() => stream,() => input);
        session.StartRecording();
        await input.Started.Task.WaitAsync(Timeout);
        using var cancellation=new CancellationTokenSource();
        var stop=session.StopRecordingAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        await input.Disposed.Task.WaitAsync(Timeout);
        Assert.False(stream.Finished);
    }

    [Fact]
    public async Task FullQueueCannotLeaveInputOpenWhenStopCannotBeQueued()
    {
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input=new FakeCapture();
        var stream=new FakeStream { OnAccept=() => {
            entered.TrySetResult();
            if(!release.Wait(Timeout)) throw new TimeoutException();
        } };
        using var session=new SpeechSession(() => stream,() => input);
        try {
            session.StartRecording();
            await input.Started.Task.WaitAsync(Timeout);
            input.Emit(new float[8960]);
            await entered.Task.WaitAsync(Timeout);
            for(int i=0;i<256;i++) input.Emit([.1f]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopRecordingAsync().WaitAsync(Timeout));
        } finally { release.Set(); }
        await input.Disposed.Task.WaitAsync(Timeout);
        Assert.False(stream.Finished);
        Assert.Equal(1,input.Disposes);
    }

    [Fact]
    public async Task DisposeClosesActiveInput()
    {
        var input=new FakeCapture();
        var session=new SpeechSession(() => new FakeStream(),() => input);
        session.StartRecording();
        await input.Started.Task.WaitAsync(Timeout);
        session.Dispose(); session.Dispose();
        Assert.Equal(1,input.Stops); Assert.Equal(1,input.Disposes);

    }

    sealed class FakeCapture : IMicrophoneCapture
    {
        public event Action<float[]>? Samples;
        public event Action<Exception>? Failed;
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnStart {get;init;}
        public Action? OnStop {get;set;}
        public Action? OnDispose {get;init;}
        public int Starts,Stops,Disposes;
        public void Start() { Interlocked.Increment(ref Starts); Started.TrySetResult(); OnStart?.Invoke(); }
        public void Stop() { Interlocked.Increment(ref Stops); OnStop?.Invoke(); }
        public void Dispose() { Interlocked.Increment(ref Disposes); Disposed.TrySetResult(); OnDispose?.Invoke(); }
        public void Emit(float[] samples) => Samples?.Invoke(samples);
        public void Fail(Exception error) => Failed?.Invoke(error);
    }

    sealed class FakeStream : ISpeechStream
    {
        public List<float> Samples {get;}=[];
        public Action? OnFinish {get;set;}
        public Action? OnAccept {get;init;}
        public Action? OnDispose {get;init;}
        public bool Finished {get;private set;}
        public int Decodes {get;private set;}
        public void Accept(float[] samples) { OnAccept?.Invoke(); Samples.AddRange(samples); }
        public void InputFinished() { OnFinish?.Invoke(); Finished=true; }
        public string Decode() { Decodes++; return "synthetic result"; }
        public void Dispose() => OnDispose?.Invoke();
    }
}
