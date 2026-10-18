using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Typr.Infrastructure;
using Xunit;

namespace Typr.Infrastructure.Tests;

public sealed class NativeCaptureDrainTests
{
    static readonly TimeSpan Timeout=TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StopDrainsUnreadNativePacketsAndFlushesLastFrameBeforeInputFinished()
    {
        var source=new FakeSource();
        var input=new SharedMicrophoneCapture(source.Open);
        var stream=new FakeStream();
        float[] expected=[.25f,.5f,0,0,0,.75f];
        stream.OnFinish=() => {
            Assert.True(source.Disposed);
            Assert.Equal(expected,stream.Samples.Take(expected.Length));
            Assert.Equal(expected.Length+16000,stream.Samples.Count);
        };
        using var session=new SpeechSession(() => stream,() => input);
        session.StartRecording();
        await source.Started.Task.WaitAsync(Timeout);
        // The pump is sleeping. These packets remain unread until stop wakes it.
        source.Queue([.25f,.5f]);
        source.QueueSilence(3);
        source.Queue([.75f]);
        Assert.Equal("complete ending",await session.StopRecordingAsync().WaitAsync(Timeout));
        Assert.Equal(expected.Length,session.CapturedSampleCount);
        Assert.Equal(new[] {"start","stop","get","release","get","release","get","release","dispose"},source.Events);
        Assert.True(stream.Finished);
        Assert.Equal(1,source.Disposes);
    }

    [Fact]
    public async Task StartWaitsForNativeStartAndSourceLivesOnTheCaptureThread()
    {
        using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source=new FakeSource { OnStart=() => {
            entered.TrySetResult();
            if(!release.Wait(Timeout)) throw new TimeoutException();
        } };
        using var input=new SharedMicrophoneCapture(source.Open);
        int callerThread=0;
        var starting=Task.Run(() => { callerThread=Environment.CurrentManagedThreadId; input.Start(); });
        try {
            await entered.Task.WaitAsync(Timeout);
            Assert.False(starting.IsCompleted);
            Assert.NotEqual(callerThread,source.OwnerThread);
        }
        finally { release.Set(); }
        await starting.WaitAsync(Timeout);
        input.Stop(); input.Dispose(); input.Dispose();
        Assert.Equal(new[] {"start","stop","dispose"},source.Events);
        Assert.Equal(1,source.Disposes);
    }

    [Fact]
    public void ConstructionAndDisposalWithoutStartNeverOpenNativeInput()
    {
        int opens=0;
        using var input=new SharedMicrophoneCapture(() => { opens++; return new FakeSource().Open(); });
        input.Dispose();
        Assert.Equal(0,opens);
        Assert.Throws<ObjectDisposedException>(input.Start);
    }

    [Fact]
    public void NativeStartFailureClosesSourceAndReachesCaller()
    {
        var source=new FakeSource { OnStart=() => throw new InvalidOperationException("native start failed") };
        var errors=new ConcurrentQueue<Exception>();
        using var input=new SharedMicrophoneCapture(source.Open);
        input.Failed+=errors.Enqueue;
        Assert.Equal("native start failed",Assert.Throws<InvalidOperationException>(input.Start).Message);
        input.Dispose();
        Assert.Equal("native start failed",Assert.Single(errors).Message);
        Assert.True(source.Disposed);
        Assert.Equal(1,source.Disposes);
    }

    [Fact]
    public async Task FinalNativeReadFailureClosesInputAndPreventsSuccessfulFinalization()
    {
        var source=new FakeSource { FailRead=true };
        var input=new SharedMicrophoneCapture(source.Open);
        var stream=new FakeStream();
        using var session=new SpeechSession(() => stream,() => input);
        session.StartRecording();
        await source.Started.Task.WaitAsync(Timeout);
        source.Queue([.75f]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopRecordingAsync().WaitAsync(Timeout));
        Assert.True(source.Disposed);
        Assert.False(stream.Finished);
        Assert.Equal(new[] {"start","stop","get","dispose"},source.Events);
    }

    [Fact]
    public void PacketIsReleasedBeforeSampleConsumerFailureClosesSource()
    {
        var source=new FakeSource();
        var errors=new ConcurrentQueue<Exception>();
        using var input=new SharedMicrophoneCapture(source.Open);
        input.Failed+=errors.Enqueue;
        input.Samples+=_ => throw new InvalidOperationException("sample consumer failed");
        input.Start();
        source.Queue([.25f,.5f]);
        input.Stop();
        Assert.Equal("sample consumer failed",Assert.Single(errors).Message);
        Assert.True(source.Disposed);
        Assert.Equal(new[] {"start","stop","get","release","dispose"},source.Events);
    }

    sealed class FakeSource : INativeCaptureSource
    {
        readonly ConcurrentQueue<(byte[] Bytes,int Frames,bool Silent)> packets=new();
        readonly WaveFormat format=WaveFormat.CreateIeeeFloatWaveFormat(16000,1);
        IntPtr buffer;
        int frames;
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events {get;}=[];
        public Action? OnStart {get;init;}
        public bool FailRead {get;init;}
        public int OwnerThread {get;private set;}
        public bool Disposed {get;private set;}
        public int Disposes {get;private set;}
        public INativeCaptureSource Open() { OwnerThread=Environment.CurrentManagedThreadId; return this; }
        void CheckThread() => Assert.Equal(OwnerThread,Environment.CurrentManagedThreadId);
        public WaveFormat WaveFormat { get { CheckThread(); return format; } }
        public int PollMilliseconds { get { CheckThread(); return 60000; } }
        public void Start() { CheckThread(); Events.Add("start"); OnStart?.Invoke(); Started.TrySetResult(); }
        public void Stop() { CheckThread(); Events.Add("stop"); Assert.Equal(0,frames); }
        public void Queue(float[] values) => packets.Enqueue((values.SelectMany(BitConverter.GetBytes).ToArray(),values.Length,false));
        public void QueueSilence(int count) => packets.Enqueue(([],count,true));
        public int GetNextPacketSize() { CheckThread(); return packets.TryPeek(out var packet) ? packet.Frames : 0; }
        public IntPtr GetBuffer(out int count,out AudioClientBufferFlags flags)
        {
            CheckThread(); Events.Add("get");
            if(FailRead) throw new InvalidOperationException("native read failed");
            Assert.Equal(0,frames);
            Assert.True(packets.TryPeek(out var packet));
            frames=count=packet.Frames;
            flags=packet.Silent ? AudioClientBufferFlags.Silent : 0;
            if(!packet.Silent) {
                buffer=Marshal.AllocHGlobal(packet.Bytes.Length);
                Marshal.Copy(packet.Bytes,0,buffer,packet.Bytes.Length);
            }
            return buffer;
        }
        public void ReleaseBuffer(int count)
        {
            CheckThread(); Events.Add("release");
            Assert.Equal(frames,count);
            Assert.True(packets.TryDequeue(out _));
            Marshal.FreeHGlobal(buffer); buffer=IntPtr.Zero; frames=0;
        }
        public void Dispose()
        {
            CheckThread(); Events.Add("dispose");
            Assert.Equal(0,frames);
            Disposed=true; Disposes++;
        }
    }

    sealed class FakeStream : ISpeechStream
    {
        public List<float> Samples {get;}=[];
        public Action? OnFinish {get;set;}
        public bool Finished {get;private set;}
        public void Accept(float[] samples) => Samples.AddRange(samples);
        public void InputFinished() { OnFinish?.Invoke(); Finished=true; }
        public string Decode() => "complete ending";
        public void Dispose() { }
    }
}
