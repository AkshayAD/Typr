using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.Wave;
using SherpaOnnx;
namespace Typr.Infrastructure;

public sealed record SpeechRecordingResult(string Text, string? AudioFileName, int Generation);

/// <summary>One model worker; deliberate microphone captures never wait for inference.</summary>
public sealed class SpeechSession : IDisposable
{
    readonly OnlineRecognizer? recognizer;
    readonly NeMoSpeechEngine? gpuEngine;
    readonly Func<ISpeechStream> createStream;
    readonly Func<IMicrophoneCapture> createCapture;
    readonly BlockingCollection<RecordingContext> work = new(256);
    readonly Task worker;
    readonly object sessionGate = new();
    readonly object audioGate = new();
    readonly Dictionary<int, RecordingContext> recordings = [];
    internal const int MaximumBufferedAudioSamples = 16000 * 120;
    int bufferedAudioSamples, nextGeneration;
    RecordingContext? current;
    volatile bool disposed;
    float[]? replaySamples;
    int savedAudioLimit;
    internal long CapturedSampleCount { get { var context=Volatile.Read(ref current); return context==null ? 0 : Interlocked.Read(ref context.AdmittedSamples); } }
    public string? LastAudioFileName => Volatile.Read(ref current)?.AudioFileName;
    public string BackendDescription { get; } = "CPU";
    public string? GpuFallbackReason { get; }
    public int SavedAudioLimit { get => Volatile.Read(ref savedAudioLimit); set => Volatile.Write(ref savedAudioLimit, Math.Clamp(value, 0, 200)); }
    public event Action<string>? Caption;
    public event Action<int, string>? CaptionForRecording;
    public event Action<float>? Level;
    public event Action<int, float>? LevelForRecording;
    public event Action<string>? Error;
    public event Action<int, string>? ErrorForRecording;
    /// <summary>Actual microphone lifetime, tagged to its recording. Replay never raises this event.</summary>
    public event Action<int,bool>? CaptureStateChanged;
    /// <summary>First admitted PCM block, tagged to its recording and raised before inference.</summary>
    public event Action<int>? InputReady;
    public SpeechSession(string modelDirectory, int threads = 2, int savedAudioLimit = 0, string? inputDeviceId = null,
        string? gpuDirectory = null, bool gpuFast = false, ModelLocations? locations = null)
    {
        SavedAudioLimit = savedAudioLimit;
        if (!string.IsNullOrWhiteSpace(gpuDirectory)) {
            try {
                if (!NeMoSpeechDownload.IsReady(gpuDirectory) || !NeMoSpeechDownload.VerifyAsync(gpuDirectory).GetAwaiter().GetResult())
                    throw new IOException("GPU speech files are missing or failed verification.");
                (locations ?? new ModelLocations()).RememberVerifiedGpu(gpuDirectory);
                gpuEngine = new NeMoSpeechEngine(Path.Combine(gpuDirectory, NeMoSpeechDownload.ModelFileName),
                    Path.Combine(gpuDirectory, NeMoSpeechDownload.NativeDirectoryName), useGpu: true, rightContext: gpuFast ? 1 : 6);
                gpuEngine.Warmup();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or
                EntryPointNotFoundException or BadImageFormatException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) {
                gpuEngine?.Dispose(); gpuEngine = null;
                GpuFallbackReason = "GPU speech could not start. CPU speech is being used; check the GPU files and NVIDIA driver.";
            }
        }
        if (gpuEngine != null) {
            createStream = gpuEngine.CreateStream;
            BackendDescription = $"NVIDIA CUDA · {(gpuFast ? "160 ms fast" : "560 ms quality")}";
        } else {
        if (!ModelDownload.IsReady(modelDirectory)) throw new FileNotFoundException(GpuFallbackReason == null
            ? "Download the speech model first." : "GPU speech could not start. Download the CPU speech model for fallback, or check the GPU files and NVIDIA driver.");
        if(!ModelDownload.VerifyAsync(modelDirectory).GetAwaiter().GetResult()) throw new IOException("Speech model integrity check failed. Download it again.");
        (locations ?? new ModelLocations()).RememberVerifiedCpu(modelDirectory);
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000; config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDirectory,"encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDirectory,"decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDirectory,"joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDirectory,"tokens.txt");
        config.ModelConfig.NumThreads = Math.Clamp(threads,2,4);
        config.DecodingMethod = "greedy_search"; config.EnableEndpoint = 0;
        var providerConfig = Path.GetTempFileName();
        try {
            // Sleeping between inference tasks avoids spending CPU on idle ORT workers.
            File.WriteAllText(providerConfig,"SessionConfig.session.intra_op.allow_spinning=0\nSessionConfig.session.inter_op.allow_spinning=0\n");
            config.ModelConfig.Provider = "cpu:" + providerConfig;
            recognizer = new OnlineRecognizer(config);
        } finally {
            try { File.Delete(providerConfig); }
            catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }
        createStream = () => new SherpaSpeechStream(recognizer);
        BackendDescription = $"CPU · {Math.Clamp(threads, 2, 4)} threads";
        }
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
        foreach(var context in work.GetConsumingEnumerable()) ProcessRecording(context);
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Prepares optional synthetic replay. Never opens or enumerates microphone hardware.</summary>
    public void Prepare()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(replaySamples != null || recognizer==null && gpuEngine==null) return;
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
        lock(sessionGate) {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(current is { StopRequested: false, Cancelled: false, InputClosed: false })
                throw new InvalidOperationException("A recording is already capturing audio.");
            if(recordings.Count>=256) throw new InvalidOperationException("Too many recordings are waiting for speech processing.");
            var previous=current;
            var context=new RecordingContext(++nextGeneration,SavedAudioLimit);
            recordings.Add(context.Generation,context);
            Volatile.Write(ref current,context);
            // A deliberate restart ends the previous release tail. Drain its
            // driver packets before opening the same microphone again, so the
            // next utterance cannot be admitted by both recording contexts.
            previous?.CloseRequested.TrySetResult();
            // The producer has its own lifetime. It can admit PCM while the model
            // worker is still finishing an earlier recording on the shared engine.
            context.CaptureTask=Task.Run(() => CaptureAfterPreviousAsync(context,previous));
            work.Add(context);
            return context.Generation;
        }
    }

    async Task CaptureAfterPreviousAsync(RecordingContext context,RecordingContext? previous)
    {
        if(previous!=null) {
            try { await previous.CaptureTask.ConfigureAwait(false); }
            catch { /* The previous result reports its own producer cleanup failure. */ }
        }
        await CaptureAsync(context).ConfigureAwait(false);
    }

    public async Task<string> StopRecordingAsync(CancellationToken cancellationToken=default) =>
        (await FinishRecordingAsync(cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>Detaches the recording selected at invocation, including its immutable audio-file identity.</summary>
    public Task<SpeechRecordingResult> FinishRecordingAsync(CancellationToken cancellationToken=default)
    {
        RecordingContext? context;
        lock(sessionGate) {
            ObjectDisposedException.ThrowIf(disposed,this);
            context=current;
            if(context==null) return Task.FromResult(new SpeechRecordingResult("",null,0));
            lock(context.Gate) {
                if(!context.StopRequested) {
                    // Revoke a pending native open now, while a claimed input keeps
                    // the existing 300 ms release tail and final driver packets.
                    context.StopRequested=true;
                    _=CloseAfterTailAsync(context);
                }
            }
        }
        return AwaitResultAsync(context,cancellationToken);
    }

    async Task<SpeechRecordingResult> AwaitResultAsync(RecordingContext context,CancellationToken cancellationToken)
    {
        using var registration=cancellationToken.Register(() => CancelContext(context));
        return await context.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task CloseAfterTailAsync(RecordingContext context)
    {
        await Task.Delay(300).ConfigureAwait(false);
        context.CloseRequested.TrySetResult();
    }

    public void CancelRecording(int generation)
    {
        RecordingContext? context;
        lock(sessionGate) recordings.TryGetValue(generation,out context);
        if(context!=null) CancelContext(context);
    }

    /// <summary>Cancels all recordings, for shutdown or an explicit resource unload.</summary>
    public void CancelRecording()
    {
        RecordingContext[] contexts;
        lock(sessionGate) contexts=recordings.Values.ToArray();
        foreach(var context in contexts) CancelContext(context);
    }

    void CancelContext(RecordingContext context)
    {
        lock(context.Gate) {
            if(context.Result.Task.IsCompleted) return;
            context.Cancelled=true;
            context.StopRequested=true;
            context.CloseRequested.TrySetResult();
        }
        DiscardAudio(context);
        context.Wake.Release();
    }

    bool TryBeginInput(RecordingContext context)
    {
        lock(context.Gate) {
            if(disposed || context.Cancelled || context.StopRequested || context.Failure!=null) return false;
            context.InputClaimed=true;
            return true;
        }
    }

    async Task CaptureAsync(RecordingContext context)
    {
        IMicrophoneCapture? input=null;
        bool active=false;
        try {
            if(context.Cancelled || context.StopRequested || disposed) return;
            if(replaySamples is { } samples) {
                if(!TryBeginInput(context)) return;
                context.StartSucceeded=true;
                var playback=Stopwatch.StartNew();
                for(int offset=0;offset<samples.Length;offset+=320) {
                    if(context.CloseRequested.Task.IsCompleted) break;
                    int count=Math.Min(320,samples.Length-offset);
                    QueueAudio(context,samples.AsSpan(offset,count).ToArray());
                    var delay=TimeSpan.FromSeconds((offset+count)/16000d)-playback.Elapsed;
                    if(delay>TimeSpan.Zero) await Task.WhenAny(Task.Delay(delay),context.CloseRequested.Task).ConfigureAwait(false);
                }
                await context.CloseRequested.Task.ConfigureAwait(false);
            } else {
                input=createCapture();
                if(context.Cancelled || context.StopRequested || disposed) return;
                input.Samples += samples => QueueAudio(context,samples);
                input.Failed += error => CaptureFailed(context,error);
                if(!input.Start(() => TryBeginInput(context))) return;
                context.StartSucceeded=true;
                active=true;
                CaptureStateChanged?.Invoke(context.Generation,true);
                await context.CloseRequested.Task.ConfigureAwait(false);
            }
        }
        catch(Exception error) { CaptureFailed(context,error); }
        finally {
            // Stop and Dispose can emit the final PCM. Admit it until both finish;
            // only this context is closed, even when a newer microphone is active.
            try { input?.Stop(); }
            catch(Exception error) { CaptureFailed(context,error); }
            finally {
                try { input?.Dispose(); }
                catch(Exception error) { CaptureFailed(context,error); }
                finally {
                    context.InputClosed=true;
                    if(active) CaptureStateChanged?.Invoke(context.Generation,false);
                    context.Wake.Release();
                }
            }
        }
    }

    void CaptureFailed(RecordingContext context,Exception error)
    {
        bool publish;
        lock(context.Gate) {
            if(context.Cancelled || context.InputClosed || disposed) return;
            publish=context.Failure==null;
            context.Failure ??= error;
            context.StopRequested=true;
            context.CloseRequested.TrySetResult();
        }
        DiscardAudio(context);
        context.Wake.Release();
        if(publish) {
            ErrorForRecording?.Invoke(context.Generation,error.Message);
            Error?.Invoke(error.Message);
        }
    }

    void QueueAudio(RecordingContext context,float[] samples)
    {
        if(samples.Length==0 || disposed || context.Cancelled || context.Failure!=null || context.InputClosed || !context.InputClaimed) return;
        bool overflow=false,ready=false;
        lock(audioGate) {
            if(disposed || context.Cancelled || context.Failure!=null || context.InputClosed) return;
            if(samples.Length>MaximumBufferedAudioSamples-bufferedAudioSamples) overflow=true;
            else {
                context.Audio.Enqueue(samples); bufferedAudioSamples+=samples.Length;
                Interlocked.Add(ref context.AdmittedSamples,samples.Length);
                if(!context.Ready) { context.Ready=true; ready=true; }
            }
        }
        if(overflow) CaptureFailed(context,new InvalidOperationException("More than two minutes of captured audio is waiting for speech processing. Cancel and retry."));
        else {
            // Readiness precedes the model wake-up, including the first block when
            // this recording is waiting behind another recording's final decode.
            if(ready) {
                try { if(!context.Cancelled && context.Failure==null && !disposed) InputReady?.Invoke(context.Generation); }
                finally { context.ReadinessPublished.TrySetResult(); }
            }
            context.Wake.Release();
            PublishLevel(context,samples);
        }
    }

    void PublishLevel(RecordingContext context,float[] samples)
    {
        long now=Stopwatch.GetTimestamp(),previous=Interlocked.Read(ref context.LastLevelAt);
        if(now-previous<Stopwatch.Frequency/20 || Interlocked.CompareExchange(ref context.LastLevelAt,now,previous)!=previous) return;
        if(disposed || context.Cancelled || context.Failure!=null || context.InputClosed) return;
        float level=Math.Min(1,(float)Math.Sqrt(samples.Sum(x=>(double)x*x)/samples.Length)*4);
        LevelForRecording?.Invoke(context.Generation,level);
        Level?.Invoke(level);
    }

    void ProcessRecording(RecordingContext context)
    {
        ISpeechStream? stream=null;
        SavedAudioRecording? recording=null;
        SpeechRecordingResult? result=null;
        Exception? failure=null;
        try {
            ThrowIfUnavailable(context);
            stream=createStream();
            ThrowIfUnavailable(context);
            // Only the model worker owns writers, so retention never deletes the
            // next recording's still-open WAV while completing an older one.
            var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Typr","audio");
            recording=SavedAudioRecording.Start(directory,context.SavedAudioLimit);
            while(true) {
                ThrowIfUnavailable(context);
                float[]? samples=null;
                bool inputClosed;
                lock(audioGate) {
                    if(context.Audio.Count>0) {
                        samples=context.Audio.Dequeue(); bufferedAudioSamples-=samples.Length;
                    }
                    inputClosed=context.InputClosed;
                }
                if(samples!=null) {
                    context.ReadinessPublished.Task.GetAwaiter().GetResult();
                    ThrowIfUnavailable(context);
                    recording?.WriteSamples(samples);
                    stream.Accept(samples);
                    ThrowIfUnavailable(context);
                    Decode(context,stream);
                    continue;
                }
                if(inputClosed) break;
                context.Wake.Wait();
            }
            context.CaptureTask.GetAwaiter().GetResult();
            ThrowIfUnavailable(context);
            if(!context.StartSucceeded) result=new SpeechRecordingResult("",null,context.Generation);
            else {
                stream.Accept(new float[16000]);
                ThrowIfUnavailable(context);
                stream.InputFinished();
                ThrowIfUnavailable(context);
                string text=Decode(context,stream).Trim();
                ThrowIfUnavailable(context);
                recording?.Complete();
                context.AudioFileName=recording?.FileName;
                result=new SpeechRecordingResult(text,context.AudioFileName,context.Generation);
            }
        }
        catch(OperationCanceledException error) { failure=error; }
        catch(Exception error) {
            failure=error;
            CaptureFailed(context,error);
        }
        finally {
            // Capture shutdown is independent of a native decode. On failure this
            // also fences callbacks before deleting abandoned audio or a stream.
            if(failure!=null) context.CloseRequested.TrySetResult();
            try { context.CaptureTask.GetAwaiter().GetResult(); }
            catch(Exception error) { failure ??= error; }
            try { recording?.Dispose(); }
            catch(Exception error) { failure ??= error; }
            try { stream?.Dispose(); }
            catch(Exception error) { failure ??= error; }
            DiscardAudio(context);
            if(context.Cancelled || failure is OperationCanceledException) context.Result.TrySetCanceled(new CancellationToken(true));
            else if(failure!=null) context.Result.TrySetException(failure);
            else if(context.Failure is { } captureFailure) context.Result.TrySetException(captureFailure);
            else context.Result.TrySetResult(result!);
            lock(sessionGate) recordings.Remove(context.Generation);
        }
    }

    void DiscardAudio(RecordingContext context)
    {
        lock(audioGate) {
            while(context.Audio.TryDequeue(out var samples)) bufferedAudioSamples-=samples.Length;
        }
    }

    static void ThrowIfUnavailable(RecordingContext context)
    {
        if(context.Cancelled) throw new OperationCanceledException();
        if(context.Failure is { } error) throw error;
    }

    string Decode(RecordingContext context,ISpeechStream stream)
    {
        string text=stream.Decode();
        if(!context.Cancelled && context.Failure==null && text!=context.LastCaption) {
            context.LastCaption=text;
            CaptionForRecording?.Invoke(context.Generation,text); Caption?.Invoke(text);
        }
        return text;
    }

    public void Dispose()
    {
        lock(sessionGate) {
            if(disposed) return;
            disposed=true;
            foreach(var context in recordings.Values) CancelContext(context);
            work.CompleteAdding();
        }
        worker.GetAwaiter().GetResult(); recognizer?.Dispose(); gpuEngine?.Dispose(); work.Dispose();
    }

    sealed class RecordingContext(int generation,int savedAudioLimit)
    {
        public int Generation { get; }=generation;
        public int SavedAudioLimit { get; }=savedAudioLimit;
        public object Gate { get; }=new();
        public Queue<float[]> Audio { get; }=new();
        public SemaphoreSlim Wake { get; }=new(0);
        public TaskCompletionSource CloseRequested { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadinessPublished { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpeechRecordingResult> Result { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CaptureTask { get; set; }=Task.CompletedTask;
        public volatile bool StopRequested,Cancelled,InputClaimed,StartSucceeded,InputClosed;
        public volatile Exception? Failure;
        public bool Ready;
        public long AdmittedSamples,LastLevelAt;
        public string? LastCaption,AudioFileName;
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
