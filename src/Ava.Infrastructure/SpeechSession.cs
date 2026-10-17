using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SherpaOnnx;
namespace Ava.Infrastructure;

/// <summary>Single inference worker; microphone callbacks never run inference.</summary>
public sealed class SpeechSession : IDisposable
{
    readonly OnlineRecognizer recognizer;
    readonly BlockingCollection<Action> work = new(256);
    readonly Task worker;
    readonly Queue<float> preRoll = new();
    readonly List<float> pending = new();
    OnlineStream? stream;
    WasapiCapture? capture;
    MMDevice? device;
    SavedAudioRecording? recording;
    bool disposed;
    float[]? replaySamples; CancellationTokenSource? replayCancellation;
    volatile bool faulted;
    readonly RecordingGeneration generationGate=new();
    int activeEpoch;
    int savedAudioLimit;
    public int SavedAudioLimit { get => Volatile.Read(ref savedAudioLimit); set => Volatile.Write(ref savedAudioLimit, Math.Clamp(value, 0, 200)); }
    public event Action<string>? Caption;
    public event Action<int, string>? CaptionForRecording;
    public event Action<float>? Level;
    public event Action<string>? Error;
    public SpeechSession(string modelDirectory, int threads = 2, int savedAudioLimit = 0)
    {
        SavedAudioLimit = savedAudioLimit;
        if (!ModelDownload.IsReady(modelDirectory)) throw new FileNotFoundException("Download the speech model first.");
        if(!ModelDownload.VerifyAsync(modelDirectory).GetAwaiter().GetResult()) throw new IOException("Speech model integrity check failed. Download it again.");
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000; config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDirectory,"encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDirectory,"decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDirectory,"joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDirectory,"tokens.txt");
        config.ModelConfig.NumThreads = Math.Clamp(threads,2,4); config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search"; config.EnableEndpoint = 0;
        recognizer = new OnlineRecognizer(config);
        worker = Task.Factory.StartNew(() => { foreach(var action in work.GetConsumingEnumerable()) { try { if(stream != null && !generationGate.IsCurrent(activeEpoch)) Abort(); action(); } catch(Exception e) { faulted=true; Abort(); Error?.Invoke(e.Message); } } }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public void StartMicrophone()
    {
        if(capture != null || replaySamples != null) return;
        var testWave=Environment.GetEnvironmentVariable("AVA_TEST_WAV");
        if(!string.IsNullOrWhiteSpace(testWave)) {
            using var wave=new WaveFileReader(testWave);
            if(wave.WaveFormat.SampleRate!=16000 || wave.WaveFormat.Channels!=1 || wave.WaveFormat.BitsPerSample!=16 || wave.WaveFormat.Encoding!=WaveFormatEncoding.Pcm) throw new ArgumentException("AVA_TEST_WAV requires 16kHz mono PCM16 WAV.");
            var bytes=new byte[checked((int)wave.Length)]; int read=wave.Read(bytes,0,bytes.Length);
            replaySamples=Enumerable.Range(0,read/2).Select(i=>BitConverter.ToInt16(bytes,i*2)/32768f).ToArray(); return;
        }
        using var enumerator = new MMDeviceEnumerator();
        device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        capture = new WasapiCapture(device);
        var converter = new MicrophoneConverter(capture.WaveFormat);
        capture.DataAvailable += (_,e) => {
            var samples = converter.Convert(e.Buffer,e.BytesRecorded);
            QueueAudio(samples);
        };
        capture.RecordingStopped += (_,e) => { if(e.Exception != null) { faulted=true; Error?.Invoke(e.Exception.Message); } };
        capture.StartRecording();
    }
    public int StartRecording()
    {
        int generation=generationGate.Begin();
        int audioLimit=SavedAudioLimit;
        Enqueue(() => {
        if(!generationGate.IsCurrent(generation)) return;
        Abort(); activeEpoch=generation; faulted=false; stream=recognizer.CreateStream();
        var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Ava","audio");
        recording=SavedAudioRecording.Start(dir,audioLimit);
        if(replaySamples != null) preRoll.Clear();
        Accept(preRoll.ToArray());
        if(replaySamples != null) {
            replayCancellation=new CancellationTokenSource(); var token=replayCancellation.Token;
            _=Task.Run(async()=> { try { for(int offset=0;offset<replaySamples.Length;offset+=320) { token.ThrowIfCancellationRequested(); QueueAudio(replaySamples.Skip(offset).Take(320).ToArray()); await Task.Delay(20,token); } } catch(OperationCanceledException) { } });
        }
    });
        return generation;
    }
    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken=default)
    {
        int stoppingGeneration=generationGate.Current;
        using var cancellation=cancellationToken.Register(() => { if(generationGate.IsCurrent(stoppingGeneration)) CancelRecording(); });
        await Task.Delay(300,cancellationToken);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => {
            if(!generationGate.IsCurrent(stoppingGeneration)) { result.TrySetCanceled(new CancellationToken(true)); return; }
            try {
                if(faulted) throw new InvalidOperationException("Audio capture failed; nothing was pasted.");
                if(stream==null) { result.SetResult(""); return; }
                if(pending.Count>0) { var tail=new float[8960]; pending.CopyTo(tail); pending.Clear(); stream.AcceptWaveform(16000,tail); }
                stream.AcceptWaveform(16000,new float[16000]); stream.InputFinished(); Decode();
                var text=recognizer.GetResult(stream).Text.Trim();
                cancellationToken.ThrowIfCancellationRequested();
                if(!generationGate.IsCurrent(stoppingGeneration)) { result.TrySetCanceled(new CancellationToken(true)); return; }
                if(faulted) throw new InvalidOperationException("Audio capture failed; nothing was pasted.");
                recording?.Complete();
                result.SetResult(text);
            } catch(Exception e) { result.TrySetException(e); }
            finally { Abort(); }
        });
        return await result.Task.WaitAsync(cancellationToken);
    }
    public void CancelRecording()
    {
        // Cancellation cannot be lost behind a full audio queue. The worker checks this
        // generation before every action; a wake-up only helps when the queue is empty.
        generationGate.Cancel();
        if(disposed) return;
        try { work.TryAdd(() => { if(stream != null && !generationGate.IsCurrent(activeEpoch)) Abort(); }); } catch(InvalidOperationException) { }
    }
    void Enqueue(Action action) { ObjectDisposedException.ThrowIf(disposed,this); if(!work.TryAdd(action)) throw new InvalidOperationException("Audio queue is full."); }
    void QueueAudio(float[] samples)
    {
        if(disposed) return;
        try { if(!work.TryAdd(() => Feed(samples))) { faulted=true; Error?.Invoke("Audio processing fell behind. Cancel and retry."); } } catch(InvalidOperationException) { /* shutdown raced a capture callback */ }
    }
    void Feed(float[] samples)
    {
        Level?.Invoke(samples.Length==0 ? 0 : Math.Min(1,(float)Math.Sqrt(samples.Sum(x=>(double)x*x)/samples.Length)*4));
        foreach(var sample in samples) { preRoll.Enqueue(sample); if(preRoll.Count>4800) preRoll.Dequeue(); }
        if(stream != null && !faulted) Accept(samples);
    }
    void Accept(float[] samples)
    {
        recording?.WriteSamples(samples);
        pending.AddRange(samples);
        while(pending.Count>=8960) { var chunk=pending.GetRange(0,8960).ToArray(); pending.RemoveRange(0,8960); stream!.AcceptWaveform(16000,chunk); Decode(); }
    }
    void Decode() { while(recognizer.IsReady(stream!)) recognizer.Decode(stream!); var text=recognizer.GetResult(stream!).Text; CaptionForRecording?.Invoke(activeEpoch,text); Caption?.Invoke(text); }
    void Abort()
    {
        replayCancellation?.Cancel(); replayCancellation?.Dispose(); replayCancellation=null;
        var audio=recording; recording=null;
        try { audio?.Dispose(); }
        finally { stream?.Dispose(); stream=null; pending.Clear(); }
    }
    public void Dispose()
    {
        if(disposed)return; disposed=true; capture?.StopRecording(); capture?.Dispose(); device?.Dispose();
        work.CompleteAdding(); worker.GetAwaiter().GetResult(); Abort(); recognizer.Dispose(); work.Dispose();
    }
}

/// <summary>Stateful NAudio/WDL filtered PCM resampler. Preserves phase across WASAPI blocks.</summary>
internal sealed class MicrophoneConverter
{
    readonly WaveFormat format; readonly bool floating; readonly NAudio.Dsp.WdlResampler resampler=new();
    public MicrophoneConverter(WaveFormat format) { this.format=format; floating=format.Encoding==WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat==new Guid("00000003-0000-0010-8000-00aa00389b71"); if(format.BitsPerSample is not (16 or 24 or 32)) throw new NotSupportedException("Microphone sample format is unsupported."); resampler.SetMode(true,2,false); resampler.SetFilterParms(); resampler.SetFeedMode(true); resampler.SetRates(format.SampleRate,16000); }
    public float[] Convert(byte[] bytes,int count)
    {
        int stride=format.BitsPerSample/8; int frames=count/format.BlockAlign;
        resampler.ResamplePrepare(frames,1,out var input,out int inputOffset); int sampleIndex=0;
        for(int frame=0;frame+format.BlockAlign<=count;frame+=format.BlockAlign) {
            float sum=0;
            for(int channel=0;channel<format.Channels;channel++) { int i=frame+channel*stride; sum+=floating ? BitConverter.ToSingle(bytes,i) : stride switch { 2=>BitConverter.ToInt16(bytes,i)/32768f, 3=>((bytes[i]|bytes[i+1]<<8|bytes[i+2]<<16)<<8>>8)/8388608f, _=>BitConverter.ToInt32(bytes,i)/2147483648f }; }
            input[inputOffset+sampleIndex++]=sum/format.Channels;
        }
        var result=new float[(int)Math.Ceiling(frames*16000d/format.SampleRate)+64];
        int produced=resampler.ResampleOut(result,0,frames,result.Length,1);
        return result.AsSpan(0,produced).ToArray();
    }
}
