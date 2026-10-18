using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace Typr.Infrastructure;

public sealed record MicrophoneInput(string Id, string Name);

internal interface IMicrophoneCapture : IDisposable
{
    event Action<float[]>? Samples;
    event Action<Exception>? Failed;
    void Start();
    bool Start(Func<bool> authorizeStart)
    {
        if(!authorizeStart()) return false;
        Start(); return true;
    }
    void Stop();
}

/// <summary>Owns one shared input stream. Constructed only for an explicit recording.</summary>
internal sealed class SharedMicrophoneCapture : IMicrophoneCapture
{
    readonly Func<INativeCaptureSource> createSource;
    readonly ManualResetEventSlim stopping=new();
    readonly TaskCompletionSource<bool> started=new(TaskCreationOptions.RunContinuationsAsynchronously);
    Thread? thread;
    Func<bool>? authorizeStart;
    bool disposed;
    public event Action<float[]>? Samples;
    public event Action<Exception>? Failed;

    public SharedMicrophoneCapture(string? deviceId) : this(() => new NativeCaptureSource(deviceId)) { }
    internal SharedMicrophoneCapture(Func<INativeCaptureSource> createSource) => this.createSource=createSource;

    public void Start() => Start(() => true);
    public bool Start(Func<bool> authorizeStart)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(thread!=null) throw new InvalidOperationException("Microphone capture has already started.");
        this.authorizeStart=authorizeStart;
        thread=new Thread(Record) { IsBackground=true, Name="Typr microphone" };
        thread.Start();
        return started.Task.GetAwaiter().GetResult();
    }
    void Record()
    {
        try {
            if(stopping.IsSet) { started.TrySetResult(false); return; }
            using var source=createSource();
            var converter=new MicrophoneConverter(source.WaveFormat);
            bool running=false;
            try {
                // Device initialization can outlast a released shortcut. Claim the
                // recording only at native start, while release can still revoke it.
                if(stopping.IsSet || !authorizeStart!()) { started.TrySetResult(false); return; }
                source.Start(); running=true;
                started.TrySetResult(true);
                while(!stopping.Wait(source.PollMilliseconds)) DrainPackets(source,converter,stopWhenRequested:true);
                // Stop freezes the native stream without discarding queued packets.
                // Drain those packets before flushing conversion and releasing the join fence.
                running=false; source.Stop();
                DrainPackets(source,converter);
                var tail=converter.Flush();
                if(tail.Length>0) Samples?.Invoke(tail);
            }
            finally { if(running) source.Stop(); }
        }
        catch(Exception error) {
            started.TrySetException(error);
            Failed?.Invoke(error);
        }
    }
    void DrainPackets(INativeCaptureSource source,MicrophoneConverter converter,bool stopWhenRequested=false)
    {
        while((!stopWhenRequested || !stopping.IsSet) && source.GetNextPacketSize()>0) {
            var buffer=source.GetBuffer(out int frames,out var flags);
            byte[] bytes;
            try {
                bytes=new byte[checked(frames*source.WaveFormat.BlockAlign)];
                if((flags&AudioClientBufferFlags.Silent)==0) Marshal.Copy(buffer,bytes,0,bytes.Length);
            }
            finally { source.ReleaseBuffer(frames); }
            var samples=converter.Convert(bytes,bytes.Length);
            if(samples.Length>0) Samples?.Invoke(samples);
        }
    }
    public void Stop()
    {
        if(disposed) return;
        stopping.Set();
        thread?.Join();
    }
    public void Dispose()
    {
        if(disposed) return;
        Stop(); disposed=true; stopping.Dispose();
    }

    internal static IReadOnlyList<MicrophoneInput> EnumerateInputs()
    {
        using var enumerator=new MMDeviceEnumerator();
        var result=new List<MicrophoneInput>();
        foreach(var input in enumerator.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active)) {
            using(input) result.Add(new MicrophoneInput(input.ID,input.FriendlyName));
        }
        return result.AsReadOnly();
    }
}
