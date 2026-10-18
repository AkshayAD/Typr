using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Typr.Infrastructure;

// NeMo-Speech.cpp v0.2.0's size-prefixed stable C ABI: include/nemo_speech/asr.h.
internal sealed class NeMoSpeechApi : INeMoSpeechApi
{
    readonly NativeModule module;
    readonly CreateRecognizerCall create;
    readonly ReleaseCall destroy, close, destroyResult;
    readonly CreateStreamCall createStream;
    readonly PushCall push;
    readonly StatusCall finish;
    readonly NextCall next;
    readonly TranscriptCall transcript;
    readonly ErrorCall error;
    public SafeHandle LibraryHandle => module;

    public NeMoSpeechApi(string nativeDirectory, bool requireCuda)
    {
        if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The GPU speech backend requires Windows.");
        // ggml installs a process-wide terminate handler and cannot safely unload/reload
        // its code DLLs. Cache code only; model/stream handles still release all weights.
        module = NativeModule.Get(Path.GetFullPath(nativeDirectory));
        if(requireCuda) module.RequireCuda();
        create = Export<CreateRecognizerCall>("create");
        destroy = Export<ReleaseCall>("destroy");
        createStream = Export<CreateStreamCall>("streaming_recognize");
        close = Export<ReleaseCall>("stream_close");
        push = Export<PushCall>("stream_push_f32");
        finish = Export<StatusCall>("stream_finish");
        next = Export<NextCall>("stream_next");
        transcript = Export<TranscriptCall>("result_transcript");
        destroyResult = Export<ReleaseCall>("result_destroy");
        error = Export<ErrorCall>("last_error");
    }
    T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module.DangerousGetHandle(), "nemo_speech_asr_" + name));
    void Check(int status)
    {
        if(status != 0) throw new InvalidOperationException("GPU speech: " + (Marshal.PtrToStringUTF8(error()) ?? "Native inference failed."));
    }
    public IntPtr CreateRecognizer(string modelPath, bool useGpu, int rightContext)
    {
        if(rightContext is not (0 or 1 or 6 or 13)) throw new ArgumentOutOfRangeException(nameof(rightContext));
        var fullPath = Path.GetFullPath(modelPath);
        if(!File.Exists(fullPath)) throw new FileNotFoundException("Download the GPU speech model first.", fullPath);
        var path = Marshal.StringToCoTaskMemUTF8(fullPath);
        try {
            using var search = module.EnterSearchPath();
            var backend = new Backend { Size = (nuint)Marshal.SizeOf<Backend>(), Gpu = useGpu ? 0 : -1 };
            var model = new Model { Size = (nuint)Marshal.SizeOf<Model>(), Path = path };
            var streaming = new Streaming { Size = (nuint)Marshal.SizeOf<Streaming>(), ChunkSize = .16f, RightContext = rightContext };
            var endpointing = new Endpointing { Size = (nuint)Marshal.SizeOf<Endpointing>() };
            using var b = new NativeValue<Backend>(backend);
            using var m = new NativeValue<Model>(model);
            using var s = new NativeValue<Streaming>(streaming);
            using var e = new NativeValue<Endpointing>(endpointing);
            var config = new Config { Size = (nuint)Marshal.SizeOf<Config>(), Backend = b.Pointer, Model = m.Pointer, Streaming = s.Pointer, Endpointing = e.Pointer };
            Check(create(ref config, out var result));
            if(result == IntPtr.Zero) throw new InvalidOperationException("The GPU speech model could not be opened.");
            return result;
        } finally { Marshal.FreeCoTaskMem(path); }
    }
    public void DestroyRecognizer(IntPtr model) => destroy(model);
    public IntPtr CreateStream(IntPtr model)
    {
        // A null options pointer selects native defaults; interim results are enabled there.
        Check(createStream(model, IntPtr.Zero, out var stream));
        if(stream == IntPtr.Zero) throw new InvalidOperationException("The GPU speech stream could not be opened.");
        return stream;
    }
    public void CloseStream(IntPtr stream) => close(stream);
    public void Push(IntPtr stream, float[] samples) => Check(push(stream, samples, (nuint)samples.Length, 16000));
    public void Finish(IntPtr stream) => Check(finish(stream));
    public string? Next(IntPtr stream)
    {
        Check(next(stream, out var result));
        if(result == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(transcript(result, 0)) ?? ""; }
        finally { destroyResult(result); }
    }
    public void Dispose() { } // Code modules remain loaded until process exit.

    [StructLayout(LayoutKind.Sequential)] struct Backend { public nuint Size; public int Gpu; }
    [StructLayout(LayoutKind.Sequential)] struct Model { public nuint Size; public IntPtr Path, Name; }
    [StructLayout(LayoutKind.Sequential)] struct Streaming { public nuint Size; public float ChunkSize, LeftPadding, RightPadding; public int RightContext; }
    [StructLayout(LayoutKind.Sequential)] struct Endpointing { public nuint Size; public byte Enable, VadBased; public int History; }
    [StructLayout(LayoutKind.Sequential)] struct Config { public nuint Size; public IntPtr Backend, Model, Streaming, Decoder, Vad, Endpointing, Postproc, Diar, Batching; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateRecognizerCall(ref Config config, out IntPtr model);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateStreamCall(IntPtr model, IntPtr options, out IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ReleaseCall(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PushCall(IntPtr stream, [In] float[] samples, nuint count, int sampleRate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StatusCall(IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NextCall(IntPtr stream, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr TranscriptCall(IntPtr result, nuint alternative);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ErrorCall();

    sealed class NativeValue<T> : IDisposable where T : struct
    {
        public IntPtr Pointer { get; } = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        public NativeValue(T value) => Marshal.StructureToPtr(value, Pointer, false);
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    sealed class NativeModule : SafeHandleZeroOrMinusOneIsInvalid
    {
        readonly string directory;
        static readonly Dictionary<string, NativeModule> modules = new(StringComparer.OrdinalIgnoreCase);
        public static NativeModule Get(string directory)
        {
            lock(modules) {
                if(!modules.TryGetValue(directory, out var module)) modules.Add(directory, module = new NativeModule(directory));
                return module;
            }
        }
        public NativeModule(string directory) : base(true)
        {
            this.directory = directory;
            if(!Directory.Exists(directory)) throw new DirectoryNotFoundException("Download the GPU speech runtime first.");
            using var search = EnterSearchPath();
            var loaded = LoadLibraryEx(Path.Combine(directory, "nemo_speech_asr_c.dll"), IntPtr.Zero, 0x1100);
            if(loaded == IntPtr.Zero) {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the GPU speech runtime.");
            }
            SetHandle(loaded);
        }
        public void RequireCuda()
        {
            var cuda = LoadLibraryEx(Path.Combine(directory, "ggml-cuda.dll"), IntPtr.Zero, 0x1100);
            if(cuda == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CUDA speech is unavailable.");
            try {
                var count = Marshal.GetDelegateForFunctionPointer<DeviceCountCall>(NativeLibrary.GetExport(cuda, "ggml_backend_cuda_get_device_count"));
                if(count() < 1) throw new NotSupportedException("No compatible NVIDIA CUDA device is available.");
            } finally { FreeLibrary(cuda); }
        }
        public IDisposable EnterSearchPath() => new SearchPath(directory);
        sealed class SearchPath : IDisposable
        {
            readonly IntPtr cookie;
            public SearchPath(string directory)
            {
                cookie = AddDllDirectory(directory);
                if(cookie == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not prepare the GPU speech runtime.");
            }
            public void Dispose() => RemoveDllDirectory(cookie);
        }
        protected override bool ReleaseHandle() => FreeLibrary(handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeviceCountCall();
        [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr AddDllDirectory(string directory);
        [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RemoveDllDirectory(IntPtr cookie);
    }
}
