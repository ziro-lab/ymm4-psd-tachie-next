namespace PsdTachieNext.Core;

public sealed class CacheCapacityException(string message) : InvalidOperationException(message);

/// <summary>Bounded decoded cache. Cold I/O and completion callbacks run outside the cache lock.</summary>
public sealed class BlockCache : IDisposable
{
    private sealed class Entry(int size)
    {
        public readonly int Size = size;
        public readonly TaskCompletionSource<byte[]> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Bytes;
        public int Borrowers;
        public long Used;
    }
    private readonly CompiledDocument document;
    private readonly Func<int, byte[]> read;
    private readonly long budget;
    private readonly object gate = new();
    private readonly Dictionary<int, Entry> entries = [];
    private long clock, resident, reserved;
    private bool disposed;
    public long ResidentBytes { get { lock (gate) return resident; } }
    public long HitCount { get; private set; }
    public long MissCount { get; private set; }
    public long EvictionCount { get; private set; }
    public BlockCache(CompiledDocument document, long budgetBytes, Func<int, byte[]>? blockReader = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (budgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        this.document = document; budget = budgetBytes; read = blockReader ?? document.ReadBlock;
    }
    public BlockLease Acquire(int blockId)
    {
        Entry entry; bool load = false;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if ((uint)blockId >= (uint)document.Manifest.Blocks.Length) throw new ArgumentOutOfRangeException(nameof(blockId));
            if (entries.TryGetValue(blockId, out entry!)) HitCount++;
            else
            {
                var required = document.Manifest.Blocks[blockId].RawLength;
                var reclaimable = entries.Values.Where(e => e.Borrowers == 0 && e.Bytes is not null).Sum(e => (long)e.Size);
                if (required > budget || resident + reserved - reclaimable + required > budget)
                    throw new CacheCapacityException("必要blockの借用がcache予算を超えます。素材または予算を確認してください。");
                while (resident + reserved + required > budget)
                    Remove(entries.Where(p => p.Value.Borrowers == 0 && p.Value.Bytes is not null).MinBy(p => p.Value.Used).Key);
                entry = new Entry(required); entries.Add(blockId, entry); reserved += required; MissCount++; load = true;
            }
            entry.Used = ++clock; entry.Borrowers++;
        }
        if (load)
        {
            try
            {
                var bytes = read(blockId);
                if (bytes.Length != entry.Size) throw new InvalidDataException("Decoded block length mismatch.");
                lock (gate) { reserved -= entry.Size; resident += entry.Size; entry.Bytes = bytes; }
                entry.Ready.SetResult(bytes);
            }
            catch (Exception ex)
            {
                lock (gate) { reserved -= entry.Size; entries.Remove(blockId); }
                entry.Ready.SetException(ex);
            }
        }
        try { return new BlockLease(entry.Ready.Task.GetAwaiter().GetResult(), () => Release(blockId, entry)); }
        catch { Release(blockId, entry); throw; }
    }
    public void Trim()
    {
        lock (gate)
            foreach (var id in entries.Where(p => p.Value.Borrowers == 0 && p.Value.Bytes is not null).Select(p => p.Key).ToArray()) Remove(id);
    }
    private void Release(int id, Entry entry)
    {
        lock (gate)
            if (--entry.Borrowers == 0 && disposed && entries.GetValueOrDefault(id) == entry) Remove(id);
    }
    private void Remove(int id) { resident -= entries[id].Size; entries.Remove(id); EvictionCount++; }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; Trim(); } }
}

/// <summary>Memory is borrowed only until Dispose.</summary>
public sealed class BlockLease : IDisposable
{
    private Action? release;
    private byte[]? bytes;
    internal BlockLease(byte[] bytes, Action release) { this.bytes = bytes; this.release = release; }
    public ReadOnlyMemory<byte> Memory
    {
        get { var current = Volatile.Read(ref bytes); ObjectDisposedException.ThrowIf(current is null, this); return current; }
    }
    public void Dispose()
    {
        var action = Interlocked.Exchange(ref release, null);
        if (action is null) return;
        Interlocked.Exchange(ref bytes, null); action();
    }
}
