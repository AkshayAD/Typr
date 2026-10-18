using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

public sealed class GameModeMonitorTests
{
    [Fact]
    public void RegistrationRequestsV2AndAcceptsImmediateInitialCallback()
    {
        var native = new FakeNativeApi { InitialMode = 5 };
        using var monitor = new WindowsGameModeMonitor(native);
        Assert.Equal(2u, native.Version);
        Assert.True(monitor.IsSupported);
        Assert.True(monitor.IsGaming);
        var readings = new List<bool>();
        monitor.Changed += () => readings.Add(monitor.IsGaming);
        native.Notify(5);
        native.Notify(2);
        native.Notify(5);
        Assert.Equal(new[] { false, true }, readings);
    }

    [Fact]
    public void ConcurrentCallbacksAndQueuedConsumersReadLatestState()
    {
        var native = new FakeNativeApi();
        using var monitor = new WindowsGameModeMonitor(native);
        var queued = new System.Collections.Concurrent.ConcurrentQueue<Func<bool>>();
        monitor.Changed += () => queued.Enqueue(() => monitor.IsGaming);
        Parallel.For(0, 100, i => native.Notify(i % 2 == 0 ? 5 : 2));
        native.Notify(5);
        Assert.True(monitor.IsGaming);
        Assert.NotEmpty(queued);
        Assert.All(queued, read => Assert.True(read()));
    }

    [Fact]
    public void SubscriberExceptionDoesNotEscapeNativeCallback()
    {
        var native = new FakeNativeApi();
        using var monitor = new WindowsGameModeMonitor(native);
        monitor.Changed += () => throw new InvalidOperationException("Subscriber failure");
        native.Notify(5);
        Assert.True(monitor.IsGaming);
    }

    [Fact]
    public void ConcurrentDisposeUnregistersOnceAndIgnoresFurtherCallbacks()
    {
        var native = new FakeNativeApi();
        var monitor = new WindowsGameModeMonitor(native);
        int changed = 0;
        monitor.Changed += () => Interlocked.Increment(ref changed);
        Parallel.For(0, 100, i => { if (i % 2 == 0) native.Notify(5); else monitor.Dispose(); });
        monitor.Dispose();
        int before = changed;
        native.Notify(5);
        native.Notify(2);
        Assert.Equal(1, native.UnregisterCount);
        Assert.Equal(before, changed);
        Assert.False(monitor.IsGaming);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(unchecked((int)0x80004005))]
    public void RegistrationFailureIsUnsupportedAndDoesNotUnregister(int result)
    {
        var native = new FakeNativeApi { Result = result, InitialMode = 5 };
        var monitor = new WindowsGameModeMonitor(native);
        Assert.False(monitor.IsSupported);
        Assert.False(monitor.IsGaming);
        monitor.Dispose();
        Assert.Equal(0, native.UnregisterCount);
    }

    [Theory]
    [InlineData("dll")]
    [InlineData("entry")]
    [InlineData("image")]
    public void UnavailableNativeApiIsUnsupported(string failure)
    {
        var native = new FakeNativeApi { Failure = failure };
        using var monitor = new WindowsGameModeMonitor(native);
        Assert.False(monitor.IsSupported);
        Assert.False(monitor.IsGaming);
    }

    private sealed class FakeNativeApi : WindowsGameModeMonitor.INativeApi
    {
        public int InitialMode { get; init; } = 2;
        public int Result { get; init; }
        public string? Failure { get; init; }
        public uint Version { get; private set; }
        public int UnregisterCount;
        private WindowsGameModeMonitor.PowerModeCallback? _callback;

        public int Register(uint version, WindowsGameModeMonitor.PowerModeCallback callback, out IntPtr registration)
        {
            if (Failure == "dll") throw new DllNotFoundException();
            if (Failure == "entry") throw new EntryPointNotFoundException();
            if (Failure == "image") throw new BadImageFormatException();
            Version = version;
            _callback = callback;
            registration = Result == 0 ? new IntPtr(42) : IntPtr.Zero;
            Notify(InitialMode);
            return Result;
        }

        public int Unregister(IntPtr registration)
        {
            Assert.Equal(new IntPtr(42), registration);
            Interlocked.Increment(ref UnregisterCount);
            return 0;
        }

        public void Notify(int mode) => _callback!(mode, IntPtr.Zero);
    }
}
