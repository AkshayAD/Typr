namespace Typr.Infrastructure;
/// <summary>Out-of-band invalidation: a full sample queue cannot lose cancellation.</summary>
internal sealed class RecordingGeneration
{
    int epoch;
    public int Current => Volatile.Read(ref epoch);
    public int Begin() => Interlocked.Increment(ref epoch);
    public void Cancel() => Interlocked.Increment(ref epoch);
    public bool IsCurrent(int generation) => Volatile.Read(ref epoch)==generation;
}
