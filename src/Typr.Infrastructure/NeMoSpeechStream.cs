using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Typr.Infrastructure;

/// <summary>Owns one native Nemotron model and its recording streams.</summary>
public sealed class NeMoSpeechEngine : IDisposable
{
    readonly object gate = new();
    readonly INeMoSpeechApi api;
    readonly NeMoModelHandle model;
    readonly HashSet<NeMoSpeechStream> streams = [];
    bool disposed, warmed;

    public NeMoSpeechEngine(string modelPath, string nativeDirectory, bool useGpu = true, int rightContext = 6)
        : this(new NeMoSpeechApi(nativeDirectory, useGpu), modelPath, useGpu, rightContext) { }

    internal NeMoSpeechEngine(INeMoSpeechApi api, string modelPath, bool useGpu = true, int rightContext = 6)
    {
        this.api = api;
        try { model = new NeMoModelHandle(api, api.CreateRecognizer(modelPath, useGpu, rightContext)); }
        catch { api.Dispose(); throw; }
    }

    internal ISpeechStream CreateStream()
    {
        lock(gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            var stream = new NeMoSpeechStream(this, new NeMoStreamHandle(api, api.CreateStream(model.DangerousGetHandle()), model));
            streams.Add(stream);
            return stream;
        }
    }

    /// <summary>Initializes lazy inference resources using silence; never opens a microphone.</summary>
    public void Warmup()
    {
        lock(gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if(warmed) return;
            using var stream = CreateStream();
            stream.Accept(new float[3200]);
            stream.InputFinished();
            stream.Decode();
            warmed = true;
        }
    }

    public void Dispose()
    {
        lock(gate) {
            if(disposed) return;
            disposed = true;
            foreach(var stream in streams.ToArray()) stream.Close();
            model.Dispose();
            api.Dispose();
        }
    }

    sealed class NeMoSpeechStream(NeMoSpeechEngine owner, NeMoStreamHandle handle) : ISpeechStream
    {
        string text = "";
        bool finished, closed;
        void Check()
        {
            ObjectDisposedException.ThrowIf(closed || owner.disposed, this);
        }
        public void Accept(float[] samples)
        {
            ArgumentNullException.ThrowIfNull(samples);
            lock(owner.gate) {
                Check();
                if(finished) throw new InvalidOperationException("Speech input is already finished.");
                if(samples.Length != 0) owner.api.Push(handle.DangerousGetHandle(), samples);
            }
        }
        public void InputFinished()
        {
            lock(owner.gate) {
                Check();
                if(finished) return;
                owner.api.Finish(handle.DangerousGetHandle());
                finished = true;
            }
        }
        public string Decode()
        {
            lock(owner.gate) {
                Check();
                while(owner.api.Next(handle.DangerousGetHandle()) is { } result) text = result;
                return text;
            }
        }
        internal void Close()
        {
            if(closed) return;
            closed = true;
            handle.Dispose();
            owner.streams.Remove(this);
        }
        public void Dispose() { lock(owner.gate) Close(); }
    }
}

internal interface INeMoSpeechApi : IDisposable
{
    SafeHandle? LibraryHandle { get; }
    IntPtr CreateRecognizer(string modelPath, bool useGpu, int rightContext);
    void DestroyRecognizer(IntPtr model);
    IntPtr CreateStream(IntPtr model);
    void CloseStream(IntPtr stream);
    void Push(IntPtr stream, float[] samples);
    void Finish(IntPtr stream);
    string? Next(IntPtr stream);
}

internal sealed class NeMoModelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    readonly INeMoSpeechApi api;
    readonly SafeHandle? library;
    public NeMoModelHandle(INeMoSpeechApi api, IntPtr value) : base(true)
    {
        this.api = api; library = api.LibraryHandle;
        bool added = false; library?.DangerousAddRef(ref added);
        SetHandle(value);
    }
    protected override bool ReleaseHandle()
    {
        try { api.DestroyRecognizer(handle); return true; }
        finally { library?.DangerousRelease(); }
    }
}

internal sealed class NeMoStreamHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    readonly INeMoSpeechApi api;
    readonly NeMoModelHandle model;
    public NeMoStreamHandle(INeMoSpeechApi api, IntPtr value, NeMoModelHandle model) : base(true)
    {
        this.api = api; this.model = model;
        bool added = false; model.DangerousAddRef(ref added);
        SetHandle(value);
    }
    protected override bool ReleaseHandle()
    {
        try { api.CloseStream(handle); return true; }
        finally { model.DangerousRelease(); }
    }
}
