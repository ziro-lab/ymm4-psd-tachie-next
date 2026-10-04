using System.Text.Json;

namespace PsdTachieNext.Core;

public sealed record PoolSnapshot(int ActiveDocuments, long ResidentDecodedBytes, long BlockReadCount, int OutstandingLeases);

/// <summary>
/// Shares validated immutable compiled generations and decoded blocks across consumers.
/// Synchronous cold loads must run off the UI thread. Does not own/delete disk cache directories.
/// Published directories must never be overwritten; source updates use a new directory/generation.
/// </summary>
public sealed class SharedDocumentPool : IDisposable
{
    private sealed class Entry(string key, CompiledDocument document, long budget)
    {
        public string Key { get; } = key;
        public CompiledDocument Document { get; } = document;
        public BlockCache Cache { get; } = new(document, budget);
        public Lazy<PsdNotationIndex> Notation { get; } = new(() => new(document.Manifest));
        public List<string> Directories { get; } = [];
        public int References;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> directories = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly FormatLimits limits;
    private readonly long perDocumentBudget;
    private readonly int maxDocuments;
    private bool disposed;
    internal Action<string>? BeforeOpen { get; set; }

    public SharedDocumentPool(long decodedBytesPerDocument, int maxOpenDocuments, FormatLimits? limits = null)
    {
        if (decodedBytesPerDocument <= 0) throw new ArgumentOutOfRangeException(nameof(decodedBytesPerDocument));
        if (maxOpenDocuments <= 0) throw new ArgumentOutOfRangeException(nameof(maxOpenDocuments));
        _ = checked(decodedBytesPerDocument * maxOpenDocuments);
        perDocumentBudget = decodedBytesPerDocument; maxDocuments = maxOpenDocuments; this.limits = limits ?? new();
    }

    public SharedDocumentLease Acquire(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (directories.TryGetValue(path, out var existing)) return Borrow(existing);
        }
        // Validate outside the pool lock. Racing candidates are disposed after publication.
        BeforeOpen?.Invoke(path);
        CompiledDocument? candidate = new(path, limits);
        try
        {
            var key = CompiledFormat.Hash(JsonSerializer.SerializeToUtf8Bytes(candidate.Manifest));
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (directories.TryGetValue(path, out var existing)) return Borrow(existing);
                if (!entries.TryGetValue(key, out var entry))
                {
                    if (entries.Count >= maxDocuments)
                        throw new CacheCapacityException("Too many active compiled documents. Release consumers before loading another.");
                    entry = new Entry(key, candidate, perDocumentBudget);
                    entries.Add(key, entry); candidate = null;
                }
                directories.Add(path, entry); entry.Directories.Add(path);
                return Borrow(entry);
            }
        }
        finally { candidate?.Dispose(); }
    }
    private SharedDocumentLease Borrow(Entry entry)
    {
        entry.References++;
        return new SharedDocumentLease(entry.Document.Manifest, entry.Notation, id => AcquireBlock(entry, id), () => Release(entry));
    }
    private SharedBlockLease AcquireBlock(Entry entry, int id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed || entry.References == 0, this);
            entry.References++; // Reserve document lifetime before cold I/O.
        }
        try { return new SharedBlockLease(entry.Cache.Acquire(id), () => Release(entry)); }
        catch { Release(entry); throw; }
    }
    private void Release(Entry entry)
    {
        lock (gate)
        {
            if (--entry.References != 0) return;
            entries.Remove(entry.Key);
            foreach (var path in entry.Directories) directories.Remove(path);
            entry.Directories.Clear();
        }
        try { entry.Cache.Dispose(); } finally { entry.Document.Dispose(); }
    }

    public PoolSnapshot Snapshot()
    {
        lock (gate) return new(entries.Count, entries.Values.Sum(e => e.Cache.ResidentBytes),
            entries.Values.Sum(e => e.Document.BlockReadCount), entries.Values.Sum(e => e.References));
    }
    public void Trim()
    {
        lock (gate) foreach (var e in entries.Values) e.Cache.Trim();
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            // Existing borrows stay valid. Final lease release retires its cache and file handle.
            foreach (var e in entries.Values) e.Cache.Trim();
        }
    }
}

/// <summary>A source/palette consumer owns one lease; it never owns the shared parser or mutable expression state.</summary>
public sealed class SharedDocumentLease : IDisposable
{
    private readonly object gate = new();
    private CompiledManifest? manifest;
    private Lazy<PsdNotationIndex>? notation;
    private Func<int, SharedBlockLease>? acquire;
    private Action? release;
    internal SharedDocumentLease(CompiledManifest manifest, Lazy<PsdNotationIndex> notation, Func<int, SharedBlockLease> acquire, Action release)
    { this.manifest = manifest; this.notation = notation; this.acquire = acquire; this.release = release; }
    public CompiledManifest Manifest
    {
        get { lock (gate) { ObjectDisposedException.ThrowIf(manifest is null, this); return manifest; } }
    }
    public PsdNotationIndex Notation
    {
        get
        {
            Lazy<PsdNotationIndex> value;
            lock (gate) { ObjectDisposedException.ThrowIf(notation is null, this); value = notation; }
            // Name indexing stays outside both the lease and shared pool locks.
            // A concurrent Dispose may close the document after this snapshot; the lazy
            // factory reads only its immutable Manifest, never a stream or decoded block.
            return value.Value;
        }
    }
    public SharedBlockLease AcquireBlock(int id)
    {
        Func<int, SharedBlockLease> action;
        lock (gate) { ObjectDisposedException.ThrowIf(acquire is null, this); action = acquire; }
        return action(id);
    }
    public void Dispose()
    {
        Action? action;
        lock (gate) { action = release; release = null; acquire = null; manifest = null; notation = null; }
        action?.Invoke();
    }
}

public sealed class SharedBlockLease : IDisposable
{
    private readonly object gate = new();
    private BlockLease? lease;
    private Action? release;
    internal SharedBlockLease(BlockLease lease, Action release) { this.lease = lease; this.release = release; }
    public ReadOnlyMemory<byte> Memory
    {
        get { lock (gate) { ObjectDisposedException.ThrowIf(lease is null, this); return lease.Memory; } }
    }
    public void Dispose()
    {
        BlockLease? current; Action? action;
        lock (gate) { current = lease; lease = null; action = release; release = null; }
        try { current?.Dispose(); }
        finally { action?.Invoke(); }
    }
}
