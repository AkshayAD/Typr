using NAudio.CoreAudioApi;

namespace Typr.Infrastructure;

public sealed record MicrophoneInput(string Id, string Name);

internal interface IMicrophoneCapture : IDisposable
{
    event Action<float[]>? Samples;
    event Action<Exception>? Failed;
    void Start();
    void Stop();
}

/// <summary>Owns one shared input stream. Constructed only for an explicit recording.</summary>
internal sealed class SharedMicrophoneCapture : IMicrophoneCapture
{
    readonly MMDevice device;
    readonly WasapiCapture capture;
    readonly MicrophoneConverter converter;
    bool disposed;
    public event Action<float[]>? Samples;
    public event Action<Exception>? Failed;

    public SharedMicrophoneCapture(string? deviceId)
    {
        using var enumerator=new MMDeviceEnumerator();
        device=string.IsNullOrWhiteSpace(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        try {
            if(device.DataFlow!=DataFlow.Capture || device.State!=DeviceState.Active)
                throw new InvalidOperationException("The selected microphone is unavailable. Choose an active input in Settings.");
            capture=new WasapiCapture(device) { ShareMode=AudioClientShareMode.Shared };
            try {
                converter=new MicrophoneConverter(capture.WaveFormat);
                capture.DataAvailable += (_,e) => {
                    try { Samples?.Invoke(converter.Convert(e.Buffer,e.BytesRecorded)); }
                    catch(Exception error) { Failed?.Invoke(error); }
                };
                capture.RecordingStopped += (_,e) => { if(e.Exception!=null) Failed?.Invoke(e.Exception); };
            } catch { capture.Dispose(); throw; }
        } catch { device.Dispose(); throw; }
    }

    public void Start() => capture.StartRecording();
    public void Stop() => capture.StopRecording();
    public void Dispose()
    {
        if(disposed) return; disposed=true;
        try {
            // NAudio's Dispose joins its recording thread. All final DataAvailable
            // callbacks precede this converter flush and the session's drain fence.
            capture.Dispose();
            var tail=converter.Flush();
            if(tail.Length>0) Samples?.Invoke(tail);
        }
        finally { device.Dispose(); }
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
