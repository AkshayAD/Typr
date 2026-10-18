using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;
using SherpaOnnx;
namespace Typr.Infrastructure;

/// <summary>Single inference worker; microphone callbacks never run inference.</summary>
public sealed class SpeechSession : IDisposable
{
    readonly OnlineRecognizer? recognizer;
    readonly Func<ISpeechStream> createStream;
    readonly Func<IMicrophoneCapture> createCapture;
    readonly BlockingCollection<Action> work = new(256);
    readonly Task worker;
    readonly List<float> pending = new();
    internal const int MaximumBufferedAudioSamples = 16000 * 120;
    readonly object audioGate = new();
    readonly Queue<(int Generation, float[] Samples)> capturedAudio = new();
    int bufferedAudioSamples;
    bool audioDrainQueued;
    long lastLevelAt;
    long admittedSamples;
    internal long CapturedSampleCount => Interlocked.Read(ref admittedSamples);
    ISpeechStream? stream;
    IMicrophoneCapture? capture;
    SavedAudioRecording? recording;
    volatile bool disposed;
    float[]? replaySamples; CancellationTokenSource? replayCancellation;
    volatile bool faulted;
    readonly RecordingGeneration generationGate=new();
    int activeEpoch;
    int inputEpoch;
    int pendingInputGeneration;
    int savedAudioLimit;
    string? lastAudioFileName;
    public string? LastAudioFileName => Volatile.Read(ref lastAudioFileName);
    public int SavedAudioLimit { get => Volatile.Read(ref savedAudioLimit); set => Volatile.Write(ref savedAudioLimit, Math.Clamp(value, 0, 200)); }
    public event Action<string>? Caption;
    public event Action<int, string>? CaptionForRecording;
    public event Action<float>? Level;
    public event Action<string>? Error;
    /// <summary>Actual microphone lifetime, tagged to its recording. Replay never raises this event.</summary>
    public event Action<int,bool>? CaptureStateChanged;
    public SpeechSession(string modelDirectory, int threads = 2, int savedAudioLimit = 0, string? inputDeviceId = null)
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
        createStream = () => new SherpaSpeechStream(recognizer);
        createCapture = () => new SharedMicrophoneCapture(inputDeviceId);
        worker = StartWorker();
    }
    internal SpeechSession(Func<ISpeechStream> createStream, Func<IMicrophoneCapture> createCapture)
    {
        this.createStream = createStream;
        this.createCapture = createCapture;
        worker = StartWorker();
    }
    Task StartWorker() => Task.Factory.StartNew(() => {
        foreach(var action in work.GetConsumingEnumerable()) {
            try { if(stream != null && (faulted || !generationGate.IsCurrent(activeEpoch))) Abort(); action(); }
            catch(Exception e) { faulted=true; Abort(); if(!disposed && generationGate.IsCurrent(activeEpoch)) Error?.Invoke(e.Message); }
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Prepares optional synthetic replay. Never opens or enumerates microphone hardware.</summary>
    public void Prepare()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(replaySamples != null || recognizer==null) return;
        var testWave=Environment.GetEnvironmentVariable("TYPR_REPLAY_WAV");
        if(!string.IsNullOrWhiteSpace(testWave)) {
            using var wave=new WaveFileReader(testWave);
            if(wave.WaveFormat.SampleRate!=16000 || wave.WaveFormat.Channels!=1 || wave.WaveFormat.BitsPerSample!=16 || wave.WaveFormat.Encoding!=WaveFormatEncoding.Pcm) throw new ArgumentException("TYPR_REPLAY_WAV requires 16kHz mono PCM16 WAV.");
            var bytes=new byte[checked((int)wave.Length)]; int read=wave.Read(bytes,0,bytes.Length);
            replaySamples=Enumerable.Range(0,read/2).Select(i=>BitConverter.ToInt16(bytes,i*2)/32768f).ToArray(); return;
        }
    }
    public static IReadOnlyList<MicrophoneInput> EnumerateMicrophones() => SharedMicrophoneCapture.EnumerateInputs();
    public int StartRecording()
    {
        int generation=generationGate.Begin();
        Volatile.Write(ref pendingInputGeneration,generation);
        Volatile.Write(ref lastAudioFileName, null);
        Volatile.Write(ref lastLevelAt, 0);
        Interlocked.Exchange(ref admittedSamples, 0);
        int audioLimit=SavedAudioLimit;
        Enqueue(() => {
        if(!generationGate.IsCurrent(generation)) return;
        Abort(); activeEpoch=generation; faulted=false; stream=createStream();
        if(!generationGate.IsCurrent(generation)) { Abort(); return; }
        if(Volatile.Read(ref pendingInputGeneration)!=generation) { Abort(); return; }
        var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Typr","audio");
        recording=SavedAudioRecording.Start(dir,audioLimit);
        if(!generationGate.IsCurrent(generation)) { Abort(); return; }
        if(Volatile.Read(ref pendingInputGeneration)!=generation) { Abort(); return; }
        if(replaySamples != null) {
            if(!TryBeginInput(generation)) { Abort(); return; }
            Volatile.Write(ref inputEpoch,generation);
            replayCancellation=new CancellationTokenSource(); var token=replayCancellation.Token;
            _=Task.Run(async()=> {
                try {
                    var playback=Stopwatch.StartNew();
                    for(int offset=0;offset<replaySamples.Length;offset+=320) {
                        token.ThrowIfCancellationRequested();
                        int count=Math.Min(320,replaySamples.Length-offset);
                        QueueAudio(generation,replaySamples.AsSpan(offset,count).ToArray());
                        // Absolute pacing prevents Windows timer rounding from adding
                        // a delay to every block and truncating a time-bounded replay.
                        var delay=TimeSpan.FromSeconds((offset+count)/16000d)-playback.Elapsed;
                        if(delay>TimeSpan.Zero) await Task.Delay(delay,token);
                    }
                } catch(OperationCanceledException) { }
            });
        } else {
            capture=createCapture();
            if(!generationGate.IsCurrent(generation)) { Abort(); return; }
            capture.Samples += samples => QueueAudio(generation,samples);
            capture.Failed += error => CaptureFailed(generation,error);
            if(!TryBeginInput(generation)) { Abort(); return; }
            Volatile.Write(ref inputEpoch,generation);
            capture.Start();
            CaptureStateChanged?.Invoke(generation,true);
            if(!generationGate.IsCurrent(generation)) Abort();
        }
    });
        return generation;
    }
    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken=default)
    {
        int stoppingGeneration=generationGate.Current;
        // Revoke a queued/pending open immediately, before waiting for the release tail.
        // A worker that already claimed the start keeps its normal captured tail.
        Interlocked.CompareExchange(ref pendingInputGeneration,0,stoppingGeneration);
        using var cancellation=cancellationToken.Register(() => { if(generationGate.IsCurrent(stoppingGeneration)) CancelRecording(); });
        await Task.Delay(300,cancellationToken);
        // Closing the producer first ensures all final WASAPI callbacks are queued
        // before finalization. No hardware remains open while the model finishes.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => {
            try { if(generationGate.IsCurrent(stoppingGeneration)) CloseCapture(); closed.TrySetResult(); }
            catch(Exception e) { faulted=true; Abort(); closed.TrySetException(e); }
        });
        await closed.Task.WaitAsync(cancellationToken);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => {
            if(!generationGate.IsCurrent(stoppingGeneration)) { result.TrySetCanceled(new CancellationToken(true)); return; }
            try {
                if(faulted) throw new InvalidOperationException("Audio capture failed; nothing was pasted.");
                if(stream==null) { result.SetResult(""); return; }
                DrainCapturedAudio(stoppingGeneration, all: true);
                cancellationToken.ThrowIfCancellationRequested();
                if(!generationGate.IsCurrent(stoppingGeneration)) { result.TrySetCanceled(new CancellationToken(true)); return; }
                if(faulted) throw new InvalidOperationException("Audio capture failed; nothing was pasted.");
                if(pending.Count>0) { var tail=pending.ToArray(); pending.Clear(); stream.Accept(tail); }
                stream.Accept(new float[16000]); stream.InputFinished();
                var text=Decode().Trim();
                cancellationToken.ThrowIfCancellationRequested();
                if(!generationGate.IsCurrent(stoppingGeneration)) { result.TrySetCanceled(new CancellationToken(true)); return; }
                if(faulted) throw new InvalidOperationException("Audio capture failed; nothing was pasted.");
                recording?.Complete();
                Volatile.Write(ref lastAudioFileName, recording?.FileName);
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
    void Enqueue(Action action) { ObjectDisposedException.ThrowIf(disposed,this); if(!work.TryAdd(action)) { CancelRecording(); throw new InvalidOperationException("Audio queue is full."); } }
    bool TryBeginInput(int generation) => generationGate.IsCurrent(generation) && Interlocked.CompareExchange(ref pendingInputGeneration,0,generation)==generation;
    void CaptureFailed(int generation, Exception error)
    {
        if(disposed || Volatile.Read(ref inputEpoch)!=generation || !generationGate.IsCurrent(generation)) return;
        faulted=true;
        try { work.TryAdd(() => { if(activeEpoch==generation) Abort(); }); } catch(InvalidOperationException) { }
        Error?.Invoke(error.Message);
    }
    void QueueAudio(int generation, float[] samples)
    {
        if(disposed || samples.Length==0 || Volatile.Read(ref inputEpoch)!=generation || !generationGate.IsCurrent(generation)) return;
        bool overflow=false, schedule=false;
        lock(audioGate) {
            if(disposed || Volatile.Read(ref inputEpoch)!=generation || !generationGate.IsCurrent(generation)) return;
            if(samples.Length > MaximumBufferedAudioSamples-bufferedAudioSamples) overflow=true;
            else {
                capturedAudio.Enqueue((generation,samples)); bufferedAudioSamples+=samples.Length;
                Interlocked.Add(ref admittedSamples,samples.Length);
                if(!audioDrainQueued) { audioDrainQueued=true; schedule=true; }
            }
        }
        if(overflow) CaptureFailed(generation,new InvalidOperationException("More than two minutes of captured audio is waiting for speech processing. Cancel and retry."));
        else {
            if(schedule) QueueAudioDrain(generation);
            PublishLevel(generation,samples);
        }
    }
    void PublishLevel(int generation, float[] samples)
    {
        long now=Stopwatch.GetTimestamp(), previous=Interlocked.Read(ref lastLevelAt);
        if(now-previous < Stopwatch.Frequency/20 || Interlocked.CompareExchange(ref lastLevelAt,now,previous)!=previous) return;
        if(disposed || Volatile.Read(ref inputEpoch)!=generation || !generationGate.IsCurrent(generation)) return;
        Level?.Invoke(Math.Min(1,(float)Math.Sqrt(samples.Sum(x=>(double)x*x)/samples.Length)*4));
    }
    void QueueAudioDrain(int generation)
    {
        try { if(!work.TryAdd(() => DrainCapturedAudio(generation))) CaptureFailed(generation,new InvalidOperationException("Audio processing fell behind. Cancel and retry.")); }
        catch(InvalidOperationException) { /* shutdown raced a capture callback */ }
    }
    void DrainCapturedAudio(int generation, bool all=false)
    {
        do {
            (int Generation, float[] Samples) block;
            lock(audioGate) {
                if(activeEpoch!=generation || !generationGate.IsCurrent(generation)) return;
                if(capturedAudio.Count==0) { audioDrainQueued=false; return; }
                block=capturedAudio.Dequeue(); bufferedAudioSamples-=block.Samples.Length;
            }
            Feed(block.Generation,block.Samples);
        } while(all && !faulted && generationGate.IsCurrent(generation));
        bool schedule=false;
        lock(audioGate) {
            if(activeEpoch==generation) {
                schedule=!faulted && generationGate.IsCurrent(generation) && capturedAudio.Count>0;
                if(!schedule) audioDrainQueued=false;
            }
        }
        // One wake-up per recording yields between blocks to stop/cancel controls.
        if(schedule) QueueAudioDrain(generation);
    }
    void Feed(int generation, float[] samples)
    {
        if(stream==null || faulted || activeEpoch!=generation || !generationGate.IsCurrent(generation)) return;
        Accept(samples);
    }
    void Accept(float[] samples)
    {
        recording?.WriteSamples(samples);
        pending.AddRange(samples);
        while(pending.Count>=8960) { var chunk=pending.GetRange(0,8960).ToArray(); pending.RemoveRange(0,8960); stream!.Accept(chunk); Decode(); }
    }
    string Decode() { var text=stream!.Decode(); if(generationGate.IsCurrent(activeEpoch)) { CaptionForRecording?.Invoke(activeEpoch,text); Caption?.Invoke(text); } return text; }
    void CloseCapture()
    {
        replayCancellation?.Cancel(); replayCancellation?.Dispose(); replayCancellation=null;
        var input=capture; capture=null;
        try { input?.Stop(); }
        finally {
            try { input?.Dispose(); }
            finally {
                Volatile.Write(ref inputEpoch,0);
                if(input!=null) CaptureStateChanged?.Invoke(activeEpoch,false);
            }
        }
    }
    void Abort()
    {
        var audio=recording; recording=null;
        try { CloseCapture(); }
        finally {
            try { audio?.Dispose(); }
            finally {
                try { stream?.Dispose(); }
                finally {
                    stream=null; pending.Clear();
                    lock(audioGate) { capturedAudio.Clear(); bufferedAudioSamples=0; audioDrainQueued=false; }
                }
            }
        }
    }
    public void Dispose()
    {
        if(disposed)return; disposed=true; generationGate.Cancel();
        work.CompleteAdding(); worker.GetAwaiter().GetResult(); Abort(); recognizer?.Dispose(); work.Dispose();
    }
}

internal interface ISpeechStream : IDisposable
{
    void Accept(float[] samples);
    void InputFinished();
    string Decode();
}

internal sealed class SherpaSpeechStream(OnlineRecognizer recognizer) : ISpeechStream
{
    readonly OnlineStream stream=recognizer.CreateStream();
    public void Accept(float[] samples) => stream.AcceptWaveform(16000,samples);
    public void InputFinished() => stream.InputFinished();
    public string Decode() { while(recognizer.IsReady(stream)) recognizer.Decode(stream); return recognizer.GetResult(stream).Text; }
    public void Dispose() => stream.Dispose();
}

/// <summary>Stateful NAudio/WDL filtered PCM resampler. Preserves phase across WASAPI blocks.</summary>
internal sealed class MicrophoneConverter
{
    readonly WaveFormat format; readonly bool floating; readonly NAudio.Dsp.WdlResampler resampler=new();
    bool finished;
    long inputFrames, outputSamples;
    public MicrophoneConverter(WaveFormat format) { this.format=format; floating=format.Encoding==WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat==new Guid("00000003-0000-0010-8000-00aa00389b71"); if(format.BitsPerSample is not (16 or 24 or 32)) throw new NotSupportedException("Microphone sample format is unsupported."); resampler.SetMode(true,2,false); resampler.SetFilterParms(); resampler.SetFeedMode(true); resampler.SetRates(format.SampleRate,16000); }
    public float[] Convert(byte[] bytes,int count)
    {
        if(finished) throw new InvalidOperationException("Audio conversion has already finished.");
        int stride=format.BitsPerSample/8; int frames=count/format.BlockAlign;
        inputFrames+=frames;
        resampler.ResamplePrepare(frames,1,out var input,out int inputOffset); int sampleIndex=0;
        for(int frame=0;frame+format.BlockAlign<=count;frame+=format.BlockAlign) {
            float sum=0;
            for(int channel=0;channel<format.Channels;channel++) { int i=frame+channel*stride; sum+=floating ? BitConverter.ToSingle(bytes,i) : stride switch { 2=>BitConverter.ToInt16(bytes,i)/32768f, 3=>((bytes[i]|bytes[i+1]<<8|bytes[i+2]<<16)<<8>>8)/8388608f, _=>BitConverter.ToInt32(bytes,i)/2147483648f }; }
            input[inputOffset+sampleIndex++]=sum/format.Channels;
        }
        // Bound output by cumulative input duration. WDL's feed mode can otherwise
        // overshoot fractional packet boundaries and accumulate extra samples.
        var result=new float[checked((int)(inputFrames*16000/format.SampleRate-outputSamples))];
        int produced=resampler.ResampleOut(result,0,frames,result.Length,1);
        outputSamples+=produced;
        return result.AsSpan(0,produced).ToArray();
    }
    public float[] Flush()
    {
        if(finished) return [];
        finished=true;
        // Supplying fewer input frames than requested signals WDL's end of input.
        // This releases the last fractional/interpolation frame without recording new audio.
        var result=new float[checked((int)(inputFrames*16000/format.SampleRate-outputSamples))];
        if(result.Length==0) return result;
        resampler.ResamplePrepare(1,1,out _,out _);
        int produced=resampler.ResampleOut(result,0,0,result.Length,1);
        outputSamples+=produced;
        return result.AsSpan(0,produced).ToArray();
    }
}
