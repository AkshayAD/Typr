using System.Collections.Concurrent;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

public sealed class StreamingCadenceTests
{
    static readonly TimeSpan Timeout=TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SmallPacketReachesSpeechStreamImmediatelyAndStopPreservesExactPcm()
    {
        var input=new FakeCapture(); var stream=new FakeStream();
        float[] first=[.125f,-.25f,.5f];
        float[] final=[.75f,-.5f];
        input.OnStop=() => input.Emit(final);
        stream.OnFinish=() => {
            Assert.True(input.Disposed);
            Assert.Equal(first.Concat(final),stream.Samples.Take(first.Length+final.Length));
            Assert.Equal(first.Length+final.Length+16000,stream.Samples.Count);
            Assert.All(stream.Samples.Skip(first.Length+final.Length),sample=>Assert.Equal(0,sample));
        };
        using var session=new SpeechSession(() => stream,() => input);
        session.StartRecording(); await input.Started.Task.WaitAsync(Timeout);
        input.Emit(first);
        await stream.Accepted.Task.WaitAsync(Timeout);
        Assert.Equal(first,stream.Samples);
        Assert.Equal("same result",await session.StopRecordingAsync().WaitAsync(Timeout));
        Assert.Equal(first.Length+final.Length,session.CapturedSampleCount);
        Assert.True(stream.Finished);
    }

    [Fact]
    public async Task IdenticalPartialAndFinalResultsPublishOnlyWhenCaptionChanges()
    {
        var input=new FakeCapture();
        string[] results=["","hello","hello","hello world","hello world","hello world"];
        var stream=new FakeStream { Result=decode=>results[Math.Min(decode,results.Length-1)] };
        var captions=new ConcurrentQueue<string>();
        var tagged=new ConcurrentQueue<(int Generation,string Text)>();
        using var session=new SpeechSession(() => stream,() => input);
        session.Caption+=captions.Enqueue;
        session.CaptionForRecording+=(generation,text)=>tagged.Enqueue((generation,text));
        int epoch=session.StartRecording(); await input.Started.Task.WaitAsync(Timeout);
        for(int i=0;i<5;i++) input.Emit([.25f]);
        Assert.Equal("hello world",await session.StopRecordingAsync().WaitAsync(Timeout));
        Assert.Equal(new[] {"","hello","hello world"},captions);
        Assert.Equal(new[] {(epoch,""),(epoch,"hello"),(epoch,"hello world")},tagged);
    }

    [Fact]
    public async Task SameCaptionPublishesAgainForTheNextRecording()
    {
        var first=new FakeCapture(); var next=new FakeCapture();
        var inputs=new Queue<FakeCapture>([first,next]);
        var tagged=new ConcurrentQueue<(int Generation,string Text)>();
        using var session=new SpeechSession(() => new FakeStream(),() => inputs.Dequeue());
        session.CaptionForRecording+=(generation,text)=>tagged.Enqueue((generation,text));
        int previous=session.StartRecording(); await first.Started.Task.WaitAsync(Timeout);
        first.Emit([.25f]);
        Assert.Equal("same result",await session.StopRecordingAsync().WaitAsync(Timeout));
        int current=session.StartRecording(); await next.Started.Task.WaitAsync(Timeout);
        next.Emit([.5f]);
        Assert.Equal("same result",await session.StopRecordingAsync().WaitAsync(Timeout));
        Assert.Equal(new[] {(previous,"same result"),(current,"same result")},tagged);
    }

    sealed class FakeCapture : IMicrophoneCapture
    {
        public event Action<float[]>? Samples;
        public event Action<Exception>? Failed { add {} remove {} }
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnStop {get;set;}
        public bool Disposed {get;private set;}
        public void Start()=>Started.TrySetResult();
        public void Emit(float[] samples)=>Samples?.Invoke(samples);
        public void Stop()=>OnStop?.Invoke();
        public void Dispose()=>Disposed=true;
    }

    sealed class FakeStream : ISpeechStream
    {
        public List<float> Samples {get;}=[];
        public TaskCompletionSource Accepted {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<int,string> Result {get;init;}=_ => "same result";
        public Action? OnFinish {get;set;}
        public bool Finished {get;private set;}
        int decodes;
        public void Accept(float[] samples) { Samples.AddRange(samples); Accepted.TrySetResult(); }
        public void InputFinished() { OnFinish?.Invoke(); Finished=true; }
        public string Decode()=>Result(decodes++);
        public void Dispose() { }
    }
}
