using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Typr.Infrastructure;

/// <summary>Native operations are created, used and disposed on one capture thread.</summary>
internal interface INativeCaptureSource : IDisposable
{
    WaveFormat WaveFormat { get; }
    int PollMilliseconds { get; }
    void Start();
    void Stop();
    int GetNextPacketSize();
    IntPtr GetBuffer(out int frames,out AudioClientBufferFlags flags);
    void ReleaseBuffer(int frames);
}

internal sealed class NativeCaptureSource : INativeCaptureSource
{
    readonly MMDevice device;
    readonly AudioClient client;
    readonly AudioCaptureClient capture;
    public WaveFormat WaveFormat { get; }
    public int PollMilliseconds { get; }

    public NativeCaptureSource(string? deviceId)
    {
        using var enumerator=new MMDeviceEnumerator();
        device=string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        try {
            if(device.DataFlow!=DataFlow.Capture || device.State!=DeviceState.Active)
                throw new InvalidOperationException("The selected microphone is unavailable. Choose an active input in Settings.");
            client=device.AudioClient;
            try {
                WaveFormat=client.MixFormat;
                client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.AutoConvertPcm|AudioClientStreamFlags.SrcDefaultQuality,
                    100*10000L,0,WaveFormat,Guid.Empty);
                capture=client.AudioCaptureClient;
                PollMilliseconds=Math.Max(1,(int)(client.BufferSize*1000L/WaveFormat.SampleRate/2));
            }
            catch { client.Dispose(); throw; }
        }
        catch { device.Dispose(); throw; }
    }
    public void Start() => client.Start();
    public void Stop() => client.Stop();
    public int GetNextPacketSize() => capture.GetNextPacketSize();
    public IntPtr GetBuffer(out int frames,out AudioClientBufferFlags flags) => capture.GetBuffer(out frames,out flags);
    public void ReleaseBuffer(int frames) => capture.ReleaseBuffer(frames);
    public void Dispose()
    {
        try { client.Dispose(); }
        finally { device.Dispose(); }
    }
}
