using System.Collections.Concurrent;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

// Real producer/model scheduling with synthetic input. No microphone, model or clipboard.
public sealed class OverlappingRecordingTests
{
    static readonly TimeSpan Timeout=TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RestartDuringReleaseTailDrainsPriorInputBeforeAdmittingTheNextUtterance()
    {
        using var releaseDriver=new ManualResetEventSlim();
        using var releaseDecode=new ManualResetEventSlim();
        var closing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoding=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=new Input(); var next=new Input();
        first.OnStop=() => {
            closing.TrySetResult();
            if(!releaseDriver.Wait(Timeout)) throw new TimeoutException();
            first.Emit([.3f]);
        };
        first.OnDispose=() => first.Emit([.4f]);
        var firstStream=new Stream("first") { BlockFinal=() => {
            decoding.TrySetResult();
            if(!releaseDecode.Wait(Timeout)) throw new TimeoutException();
        } };
        var nextStream=new Stream("next");
        int inputs=0,streams=0;
        using var session=new SpeechSession(() => Interlocked.Increment(ref streams)==1 ? firstStream : nextStream,
            () => Interlocked.Increment(ref inputs)==1 ? first : next);
        session.StartRecording(); await first.Started.Task.WaitAsync(Timeout);
        first.Emit([.2f]);
        var firstResult=session.FinishRecordingAsync();
        session.StartRecording();
        try {
            await closing.Task.WaitAsync(Timeout);
            Assert.False(next.Started.Task.IsCompleted);
            releaseDriver.Set();
            await next.Started.Task.WaitAsync(Timeout);
            Assert.False(first.IsOpen);
            // Two shared WASAPI streams would hear the same next utterance.
            // Deliver physical sound to every still-open synthetic input.
            foreach(var input in new[] {first,next}) if(input.IsOpen) input.Emit([.8f]);
            await decoding.Task.WaitAsync(Timeout);
            var nextResult=session.FinishRecordingAsync();
            await next.Closed.Task.WaitAsync(Timeout);
            Assert.False(firstResult.IsCompleted);
            releaseDecode.Set();
            Assert.Equal("first",(await firstResult.WaitAsync(Timeout)).Text);
            Assert.Equal("next",(await nextResult.WaitAsync(Timeout)).Text);
            Assert.Equal(new[] {.2f,.3f,.4f},firstStream.Samples.Where(sample=>sample!=0));
            Assert.Equal(new[] {.8f},nextStream.Samples.Where(sample=>sample!=0));
        }
        finally { releaseDriver.Set(); releaseDecode.Set(); }
    }

    [Fact]
    public async Task CancellingAStartWaitingForPriorDriverClosureNeverOpensItsInputLater()
    {
        using var releaseDriver=new ManualResetEventSlim();
        var closing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=new Input(); var next=new Input();
        first.OnStop=() => {
            closing.TrySetResult();
            if(!releaseDriver.Wait(Timeout)) throw new TimeoutException();
        };
        int inputs=0;
        using var session=new SpeechSession(() => new Stream("first"),
            () => Interlocked.Increment(ref inputs)==1 ? first : next);
        session.StartRecording(); await first.Started.Task.WaitAsync(Timeout);
        var firstResult=session.FinishRecordingAsync();
        int pending=session.StartRecording();
        try {
            await closing.Task.WaitAsync(Timeout);
            session.CancelRecording(pending);
            releaseDriver.Set();
            await firstResult.WaitAsync(Timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.FinishRecordingAsync());
            Assert.Equal(1,inputs);
            Assert.False(next.Started.Task.IsCompleted);
        }
        finally { releaseDriver.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextCaptureAdmitsPcmAndClosesWhileEarlierFinalDecodeIsBlocked(bool cancelEarlier)
    {
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=new Input(); var next=new Input();
        first.OnStop=() => first.Emit([.3f]);
        next.OnStop=() => next.Emit([.6f]);
        next.OnDispose=() => next.Emit([.7f]);
        var firstStream=new Stream("first words") { BlockFinal=() => {
            entered.TrySetResult();
            if(!release.Wait(Timeout)) throw new TimeoutException();
        } };
        var nextStream=new Stream("next words");
        var ready=new ConcurrentQueue<int>();
        var active=new ConcurrentQueue<(int Generation,bool Active)>();
        int streams=0,liveStreams=0,inputs=0;
        using var session=new SpeechSession(() => {
            Assert.Equal(1,Interlocked.Increment(ref liveStreams));
            var stream=Interlocked.Increment(ref streams)==1 ? firstStream : nextStream;
            stream.OnDispose=() => Interlocked.Decrement(ref liveStreams);
            return stream;
        },() => Interlocked.Increment(ref inputs)==1 ? first : next);
        session.InputReady+=ready.Enqueue;
        session.CaptureStateChanged+=(generation,isActive) => active.Enqueue((generation,isActive));
        int previous=session.StartRecording();
        await first.Started.Task.WaitAsync(Timeout);
        first.Emit([.2f]);
        var oldResult=session.FinishRecordingAsync();
        await entered.Task.WaitAsync(Timeout);
        Assert.True(first.Closed.Task.IsCompletedSuccessfully);

        try {
            int current=session.StartRecording();
            await next.Started.Task.WaitAsync(Timeout);
            next.Emit([.5f]);
            Assert.Equal(new[] {previous,current},ready);
            Assert.Equal(1,streams); // A shared model has only one live stream.
            var nextResult=session.FinishRecordingAsync();
            await next.Closed.Task.WaitAsync(Timeout);
            Assert.False(nextResult.IsCompleted);
            Assert.Equal(new[] {(previous,true),(previous,false),(current,true),(current,false)},active);
            if(cancelEarlier) session.CancelRecording(previous);
            release.Set();

            if(cancelEarlier) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldResult);
            else Assert.Equal(new SpeechRecordingResult("first words",null,previous),await oldResult.WaitAsync(Timeout));
            Assert.Equal(new SpeechRecordingResult("next words",null,current),await nextResult.WaitAsync(Timeout));
            Assert.Equal(new[] {.2f,.3f},firstStream.Samples.Where(sample=>sample!=0));
            Assert.Equal(new[] {.5f,.6f,.7f},nextStream.Samples.Where(sample=>sample!=0));
            Assert.Equal(3,session.CapturedSampleCount);
            Assert.Equal(0,liveStreams);
            Assert.Equal(1,first.Disposes); Assert.Equal(1,next.Disposes);
            Assert.True(nextStream.Finished);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CancellingEarlierFinishTokenCannotCancelNewCapture()
    {
        var first=new Input(); var next=new Input();
        var streams=new ConcurrentQueue<Stream>();
        int inputs=0;
        using var session=new SpeechSession(() => {
            var stream=new Stream("words"); streams.Enqueue(stream); return stream;
        },() => Interlocked.Increment(ref inputs)==1 ? first : next);
        using var oldCancellation=new CancellationTokenSource();
        int previous=session.StartRecording();
        await first.Started.Task.WaitAsync(Timeout);
        var oldFinish=session.FinishRecordingAsync(oldCancellation.Token);
        int current=session.StartRecording();
        await next.Started.Task.WaitAsync(Timeout);
        oldCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldFinish);
        await first.Closed.Task.WaitAsync(Timeout);
        Assert.False(next.Closed.Task.IsCompleted);
        next.Emit([.8f]);
        var result=await session.FinishRecordingAsync().WaitAsync(Timeout);
        Assert.Equal(current,result.Generation); Assert.Equal("words",result.Text);
        Assert.NotEqual(previous,current);
        Assert.Equal(new[] {.8f},streams.Last().Samples.Where(sample=>sample!=0));
    }

    [Fact]
    public async Task CancellingQueuedAudioReleasesItsBufferBeforeTheModelCanResume()
    {
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=new Input(); var abandoned=new Input(); var next=new Input();
        var inputs=new ConcurrentQueue<Input>([first,abandoned,next]);
        var errors=new ConcurrentQueue<string>();
        var ready=new ConcurrentQueue<int>();
        int streams=0;
        using var session=new SpeechSession(() => Interlocked.Increment(ref streams)==1
            ? new Stream("first") { BlockFinal=() => {
                entered.TrySetResult();
                if(!release.Wait(Timeout)) throw new TimeoutException();
            } }
            : new Stream("next"),() => inputs.TryDequeue(out var input) ? input : throw new InvalidOperationException());
        session.Error+=errors.Enqueue;
        session.InputReady+=ready.Enqueue;
        session.StartRecording(); await first.Started.Task.WaitAsync(Timeout);
        var firstResult=session.FinishRecordingAsync();
        await entered.Task.WaitAsync(Timeout);
        try {
            int cancelled=session.StartRecording(); await abandoned.Started.Task.WaitAsync(Timeout);
            abandoned.Emit(new float[SpeechSession.MaximumBufferedAudioSamples]);
            session.CancelRecording(cancelled);
            await abandoned.Closed.Task.WaitAsync(Timeout);
            int current=session.StartRecording(); await next.Started.Task.WaitAsync(Timeout);
            next.Emit([.9f]);
            Assert.Contains(current,ready);
            Assert.Empty(errors);
            var nextResult=session.FinishRecordingAsync();
            release.Set();
            await firstResult.WaitAsync(Timeout);
            Assert.Equal("next",(await nextResult.WaitAsync(Timeout)).Text);
        } finally { release.Set(); }
    }

    sealed class Input : IMicrophoneCapture
    {
        public event Action<float[]>? Samples;
        public event Action<Exception>? Failed { add {} remove {} }
        public TaskCompletionSource Started { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnStop { get; set; }
        public Action? OnDispose { get; set; }
        public int Disposes;
        public volatile bool IsOpen;
        public void Start() { IsOpen=true; Started.TrySetResult(); }
        public void Stop() => OnStop?.Invoke();
        public void Dispose() { OnDispose?.Invoke(); IsOpen=false; ++Disposes; Closed.TrySetResult(); }
        public void Emit(float[] samples) => Samples?.Invoke(samples);
    }

    sealed class Stream(string text) : ISpeechStream
    {
        public List<float> Samples { get; }=[];
        public Action? BlockFinal { get; init; }
        public Action? OnDispose { get; set; }
        public bool Finished { get; private set; }
        public void Accept(float[] samples) => Samples.AddRange(samples);
        public void InputFinished() => Finished=true;
        public string Decode() { if(Finished) BlockFinal?.Invoke(); return text; }
        public void Dispose() => OnDispose?.Invoke();
    }
}
