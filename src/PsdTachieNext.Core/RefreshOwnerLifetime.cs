namespace PsdTachieNext.Core;

/// <summary>Runtime-only registration epoch. Undo restoring an old reference cannot restore this epoch.</summary>
public sealed class RefreshOwnerLifetime : IDisposable
{
    private long epoch;
    private int retired;
    public long Capture() => Interlocked.Read(ref epoch);
    public bool IsCurrent(long expected) => Volatile.Read(ref retired) == 0 && Capture() == expected;
    public void Invalidate() => Interlocked.Increment(ref epoch);
    public void Dispose() { Interlocked.Exchange(ref retired, 1); Invalidate(); }
}
