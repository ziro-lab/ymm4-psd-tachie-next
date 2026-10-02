using System.Text;
using PsdTachieNext.Core;

namespace PsdTachieNext.Compiler;

/// <summary>Disposable, local generations; no project data or source file is written here.</summary>
public sealed class CompiledAssetRepository
{
    private readonly string root;
    private readonly long maximumBytes;
    public string SnapshotRoot => System.IO.Path.Combine(root, "snapshots");
    public long CompilationCount => Interlocked.Read(ref compilationCount);
    private long compilationCount;
    public const string BuildIdentity = "psb-section-length-v1;PsdTachieNext.Parser;bgra8-straight-v1;tree-rgb8-d2d-v1";
    public CompiledAssetRepository(string cacheRoot, long maximumBytes = 4L * 1024 * 1024 * 1024)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.maximumBytes = maximumBytes;
        root = System.IO.Path.GetFullPath(cacheRoot);
    }
    internal void Initialize() => EnsureOwnedRoot();
    internal sealed class PreparationAdmission(FileStream exclusion, long snapshotBudget) : IDisposable
    {
        public long SnapshotBudget { get; } = snapshotBudget;
        public void Dispose() => exclusion.Dispose();
    }
    internal PreparationAdmission BeginPreparation(string sourcePath, CancellationToken token)
    {
        EnsureOwnedRoot(); var exclusion = RepositoryLock(token);
        try
        {
            CleanAbandonedWork();
            var length = new FileInfo(sourcePath).Length;
            if (length > maximumBytes) throw new CacheCapacityException("元PSDのsnapshotを保持するdisk容量がありません。");
            TrimCore(maximumBytes - length, token);
            var available = checked(maximumBytes - DiskBytes());
            if (length > available) throw new CacheCapacityException("使用中世代と元PSDのsnapshotがdisk容量予算を超えます。");
            return new(exclusion, available);
        }
        catch { exclusion.Dispose(); throw; }
    }
    // Logical payload bytes, not NTFS allocated-space accounting. Never follow links.
    private long DiskBytes()
    {
        long bytes = 0; var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop(); EnsureNoLink(directory);
            foreach (var file in Directory.GetFiles(directory)) { EnsureNoLink(file); bytes = checked(bytes + new FileInfo(file).Length); }
            foreach (var child in Directory.GetDirectories(directory)) pending.Push(child);
        }
        return bytes;
    }
    private void CleanAbandonedWork()
    {
        foreach (var (parent, prefix, primary) in new[] { (SnapshotRoot, ".snapshot-", "source.psd"),
            (System.IO.Path.Combine(root, "generations"), ".tmp-", "pixels.bin") })
        foreach (var directory in Directory.GetDirectories(parent, prefix + "*"))
        {
            var name = System.IO.Path.GetFileName(directory);
            if (!Guid.TryParseExact(name[prefix.Length..], "N", out _)) continue;
            EnsureNoLink(directory);
            if (Directory.GetDirectories(directory).Length != 0) continue;
            var files = Directory.GetFiles(directory);
            if (files.Any(f => System.IO.Path.GetFileName(f) != primary &&
                !(prefix == ".tmp-" && System.IO.Path.GetFileName(f) == "manifest.json"))) continue;
            foreach (var file in files) EnsureNoLink(file);
            try
            {
                // A live snapshot's read pin or live writer prevents this exclusive write access.
                using var exclusive = File.Exists(System.IO.Path.Combine(directory, primary))
                    ? new FileStream(System.IO.Path.Combine(directory, primary), FileMode.Open, FileAccess.ReadWrite, FileShare.Delete) : null;
                Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Text.Json.JsonException) { }
        }
    }
    private void EnsureOwnedRoot()
    {
        Directory.CreateDirectory(root);
        EnsureNoLink(root);
        Directory.CreateDirectory(System.IO.Path.Combine(root, "usage"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "generations"));
        Directory.CreateDirectory(SnapshotRoot);
        foreach (var sub in new[] { "usage", "generations", "snapshots" }) EnsureNoLink(System.IO.Path.Combine(root, sub));
    }
    private static void EnsureNoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("専用cache内のリンクは使用できません。");
    }
    public static string ContentKey(SourceFingerprint fingerprint) => CompiledFormat.Hash(Encoding.UTF8.GetBytes(
        $"{CompiledFormat.Generation(fingerprint)}\n{BuildIdentity}"));
    private string PinPath(string directory) => System.IO.Path.Combine(root, "usage", System.IO.Path.GetFileName(directory) + ".use");
    private FileStream Pin(string directory)
    {
        var path = PinPath(directory);
        try { using var create = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite); }
        catch (IOException) when (File.Exists(path)) { }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private FileStream RepositoryLock(CancellationToken token)
    {
        var path = System.IO.Path.Combine(root, "repository.lock");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { token.WaitHandle.WaitOne(25); }
        }
    }
    internal PreparedAssetLease Prepare(SourceSnapshot snapshot, PsdCompiler compiler, CancellationToken token, bool lockHeld = false)
    {
        EnsureOwnedRoot();
        var key = ContentKey(snapshot.Fingerprint);
        using var operation = lockHeld ? null : RepositoryLock(token);
        var generations = System.IO.Path.Combine(root, "generations");
        foreach (var directory in Directory.GetDirectories(generations, key + "-*"))
        {
            token.ThrowIfCancellationRequested();
            FileStream? pin = null;
            try
            {
                EnsureNoLink(directory); pin = Pin(directory);
                using var doc = new CompiledDocument(directory); doc.VerifyAll();
                if (doc.Manifest.Source != snapshot.Fingerprint) throw new InvalidDataException("Content key mismatch.");
                snapshot.VerifyOriginal(token);
                return new PreparedAssetLease(directory, key, true, pin);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or System.Text.Json.JsonException or FileNotFoundException or DirectoryNotFoundException)
            { pin?.Dispose(); } // Regenerate once; never overwrite the corrupted generation.
            catch { pin?.Dispose(); throw; }
        }
        token.ThrowIfCancellationRequested();
        var retainedBytes = TrimCore(long.MaxValue, token);
        var fixedBytes = checked(DiskBytes() - retainedBytes);
        var remaining = checked(maximumBytes - fixedBytes);
        void AdmitOutput(long bytes)
        {
            token.ThrowIfCancellationRequested();
            if (retainedBytes <= remaining - bytes) return;
            retainedBytes = TrimCore(Math.Max(0, remaining - bytes), token);
            if (retainedBytes > remaining - bytes) throw new CacheCapacityException("使用中世代と新しい出力がdisk容量予算を超えます。");
        }
        var compiled = compiler.CompileSnapshot(snapshot, generations, token, maximumOutputBytes: remaining, admitOutput: AdmitOutput);
        Interlocked.Increment(ref compilationCount);
        var published = System.IO.Path.Combine(generations, key + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(compiled, published);
        PreparedAssetLease? admitted = null;
        try
        {
            admitted = new PreparedAssetLease(published, key, false, Pin(published));
            if (DiskBytes() > maximumBytes)
                throw new CacheCapacityException("使用中世代と新素材が専用disk cacheの容量予算を超えます。不要なSourceを閉じて再読込してください。");
            return admitted;
        }
        catch { admitted?.Dispose(); Directory.Delete(published, true); throw; }
    }
    internal PreparedAssetLease Borrow(PreparedAssetLease artifact)
    {
        // The artifact itself is pinned while borrowers obtain their independent OS read lease.
        var pin = Pin(artifact.Directory);
        if (!Directory.Exists(artifact.Directory)) { pin.Dispose(); throw new IOException("Cache generation retired; prepare again."); }
        return new PreparedAssetLease(artifact.Directory, artifact.ContentKey, artifact.Reused, pin);
    }
    /// <summary>Only unused, well-formed generations under this repository are eligible. Usage files are never deleted.</summary>
    public long Trim(long maximumBytes, CancellationToken token = default)
    {
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        EnsureOwnedRoot();
        using var operation = RepositoryLock(token);
        return TrimCore(maximumBytes, token);
    }
    private long TrimCore(long maximumBytes, CancellationToken token)
    {
        EnsureOwnedRoot();
        var candidates = Directory.GetDirectories(System.IO.Path.Combine(root, "generations"))
            .Where(d => System.IO.Path.GetFileName(d).Length == 97 && CompiledFormat.IsHash(System.IO.Path.GetFileName(d)[..64]) &&
                System.IO.Path.GetFileName(d)[64] == '-' && Guid.TryParseExact(System.IO.Path.GetFileName(d)[65..], "N", out _))
            .OrderBy(d => Directory.GetLastWriteTimeUtc(d)).ToArray();
        long Size(string d) { EnsureNoLink(d); return Directory.GetFiles(d).Sum(f => { EnsureNoLink(f); return new FileInfo(f).Length; }); }
        var total = candidates.Sum(Size);
        foreach (var directory in candidates)
        {
            token.ThrowIfCancellationRequested(); if (total <= maximumBytes) break;
            try
            {
                EnsureNoLink(directory);
                if (!File.Exists(PinPath(directory)))
                {
                    // A crash between compiler publication and repository naming/pinning leaves a valid orphan.
                    using (var document = new CompiledDocument(directory))
                    {
                        var prefix = System.IO.Path.GetFileName(directory)[..64];
                        if (prefix != document.Manifest.GenerationId && prefix != ContentKey(document.Manifest.Source)) continue;
                    }
                    using var createPin = Pin(directory);
                }
                // Exclusive use-file handle stays held through retirement and deletion.
                using var exclusive = new FileStream(PinPath(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (Directory.GetFiles(directory).Any(f => System.IO.Path.GetFileName(f) is not ("manifest.json" or "pixels.bin") ||
                    (File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) || Directory.GetDirectories(directory).Length != 0) continue;
                var bytes = Size(directory); Directory.Delete(directory, true); total -= bytes;
            }
            catch (IOException) { } // Busy in this or another process: skip, never break a live pin.
            catch (UnauthorizedAccessException) { }
            catch (System.Text.Json.JsonException) { }
        }
        return total;
    }
}

public sealed class PreparedAssetLease : IDisposable
{
    private IDisposable? pin;
    public string Directory { get; }
    public string ContentKey { get; }
    public bool Reused { get; }
    internal PreparedAssetLease(string directory, string key, bool reused, IDisposable pin)
    { Directory = directory; ContentKey = key; Reused = reused; this.pin = pin; }
    /// <summary>Transfers the entire generation pin into a prepared appearance, including failure cleanup.</summary>
    public PreparedAppearanceLease PrepareAppearance(SharedDocumentPool pool, IEnumerable<int>? enabled = null,
        CancellationToken token = default) => PrepareAppearanceWithinBudget(pool,long.MaxValue,enabled,token);
    public PreparedAppearanceLease PrepareAppearanceWithinBudget(SharedDocumentPool pool,long maximumDecodedBytes,
        IEnumerable<int>? enabled = null,CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(pin is null, this);
        SharedDocumentLease? document = null;
        try
        {
            document = pool.Acquire(Directory);
            var plan = RenderPlan.Create(document.Manifest, enabled);
            if(maximumDecodedBytes<=0)throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
            if(maximumDecodedBytes!=long.MaxValue&&plan.RequiredBlockIds.Sum(id=>plan.Manifest.Blocks[id].RawLength)>maximumDecodedBytes)
                throw new CacheCapacityException("Selected-source decoded prefetch budget exceeded; real use remains available.");
            var ownedPin = Interlocked.Exchange(ref pin, null)!;
            var transferred = document; document = null;
            return PreparedAppearanceLease.Prepare(transferred, plan, ownedPin, ownsDocument: true, cancellationToken: token);
        }
        catch { document?.Dispose(); Dispose(); throw; }
    }
    public void Dispose() => Interlocked.Exchange(ref pin, null)?.Dispose();
}
