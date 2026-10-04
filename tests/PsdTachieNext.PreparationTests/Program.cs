using System.Text.Json;
using PsdTachieNext.Core;
using PsdTachieNext.Compiler;
using PsdTachieNext.Tests;
using System.Diagnostics;

if (args.Length == 2 && args[0] == "--pin")
{
    using var pin = new FileStream(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(args[1])), FileMode.Open, FileAccess.Read, FileShare.Read);
    Console.WriteLine("PINNED"); Console.Out.Flush(); Console.ReadLine(); return 0;
}
if (args.Length == 3 && args[0] == "--prepare")
{
    var decode = (string s) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s));
    var repository = new CompiledAssetRepository(decode(args[2])); using var service = new SourcePreparationService(repository);
    Console.WriteLine("READY"); Console.Out.Flush(); Console.ReadLine();
    using var lease = await service.PrepareAsync(SourceAssetRef.Create(decode(args[1])), 0);
    Console.WriteLine(JsonSerializer.Serialize(new { lease.Directory, repository.CompilationCount })); return 0;
}

var root = Path.Combine(Path.GetTempPath(), "psd-next-preparation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root); var results = new List<object>(); int failed = 0, assertions = 0;
object[] prefetchMeasurements=[];
try
{
    prefetchMeasurements=await SelectedPrefetchCases.Run(root,Case,True);
    await ExportScopeCases.Run(root,Case,True);
    await Case("refresh-owner-epoch-rejects-ABA-and-retirement", () =>
    {
        using var owner = new RefreshOwnerLifetime(); var first = owner.Capture();
        True(owner.IsCurrent(first)); owner.Invalidate(); owner.Invalidate();
        True(!owner.IsCurrent(first)); var restored = owner.Capture(); True(owner.IsCurrent(restored));
        owner.Dispose(); True(!owner.IsCurrent(restored)); owner.Dispose();
        using var independent = new RefreshOwnerLifetime(); True(independent.IsCurrent(independent.Capture()));
        return Task.CompletedTask;
    });
    await Case("A01-A02-snapshot-cache-restart-no-parser-on-hit", async () =>
    {
        var source = Source(); var original = File.ReadAllBytes(source.Path); var repository = Repository();
        using (var service = new SourcePreparationService(repository))
        using (var asset = await service.PrepareAsync(source, 0))
        { True(!asset.Reused); Equal(1L, repository.CompilationCount); }
        using (var service = new SourcePreparationService(repository))
        using (var asset = await service.PrepareAsync(source, 0))
        { True(asset.Reused); Equal(1L, repository.CompilationCount); }
        True(original.AsSpan().SequenceEqual(File.ReadAllBytes(source.Path)));
        Equal(0, Directory.GetDirectories(repository.SnapshotRoot).Length);
        True(typeof(PsdCompiler).Assembly.GetReferencedAssemblies().Any(a => a.Name == "PsdTachieNext.Parser"));
        True(!typeof(PsdCompiler).Assembly.GetReferencedAssemblies().Any(a => a.Name == "PsdParser"));
    });
    await Case("A03-same-size-mtime-different-content", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var first = await service.PrepareAsync(source, 0);
        var time = File.GetLastWriteTimeUtc(source.Path); var size = new FileInfo(source.Path).Length;
        PsdFixture.Write(source.Path, seed: 72); File.SetLastWriteTimeUtc(source.Path, time);
        Equal(size, new FileInfo(source.Path).Length);
        using var next = await service.PrepareAsync(source, service.Invalidate(source.Path));
        True(first.ContentKey != next.ContentKey); Equal(2L, repository.CompilationCount);
    });
    await Case("A04-A06-shared-job-independent-leases-waiter-cancel", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        var entered = Signal(); var resume = Signal();
        service.BeforeSnapshot = async token => { entered.TrySetResult(); await resume.Task.WaitAsync(token); };
        using var cancel = new CancellationTokenSource();
        var a = service.PrepareAsync(source, 0, cancel.Token); await entered.Task;
        var b = service.PrepareAsync(source, 0); cancel.Cancel();
        await Throws<OperationCanceledException>(async () => { using var _ = await a; });
        resume.SetResult(); using var result = await b;
        Equal(1L, repository.CompilationCount);
        using var c = await service.PrepareAsync(source, 0); True(!ReferenceEquals(result, c));
        result.Dispose(); using var document = new CompiledDocument(c.Directory); document.VerifyAll();
    });
    await Case("A05-different-paths-same-content-compile-once", async () =>
    {
        var a = Source(); var b = Source(); File.Copy(a.Path, b.Path, true);
        var repository = Repository(); using var service = new SourcePreparationService(repository);
        var tasks = new[] { service.PrepareAsync(a, 0), service.PrepareAsync(b, 0) };
        var leases = await Task.WhenAll(tasks);
        try { Equal(1L, repository.CompilationCount); Equal(leases[0].Directory, leases[1].Directory); }
        finally { foreach (var l in leases) l.Dispose(); }
    });
    await Case("A07-last-waiter-cancels-worker-and-new-waiter-is-independent", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        var entered = Signal(); var canceled = Signal(); var calls = 0;
        service.BeforeSnapshot = async token =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); } finally { canceled.TrySetResult(); }
        };
        using var cancel = new CancellationTokenSource(); var a = service.PrepareAsync(source, 0, cancel.Token);
        await entered.Task; cancel.Cancel(); await Throws<OperationCanceledException>(async () => { using var _ = await a; });
        await canceled.Task; using var b = await service.PrepareAsync(source, 0);
        Equal(1L, repository.CompilationCount); Equal(0, Directory.GetDirectories(repository.SnapshotRoot).Length);
        True(File.Exists(source.Path));
    });
    await Case("A09-original-update-after-snapshot-rejects-old-publication", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        service.AfterSnapshot = (snapshot, _) => { PsdFixture.Write(source.Path, seed: 12); return Task.CompletedTask; };
        await Throws<IOException>(async () => { using var _ = await service.PrepareAsync(source, 0); });
        Equal(0, Directory.GetDirectories(repository.SnapshotRoot).Length);
        service.AfterSnapshot = null;
        using var good = await service.PrepareAsync(source, service.Invalidate(source.Path));
        using var doc = new CompiledDocument(good.Directory); doc.VerifyAll();
    });
    await Case("A09-revision-change-after-snapshot-rejects-old-result", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        service.AfterSnapshot = (_, _) => { service.Invalidate(source.Path); return Task.CompletedTask; };
        await Throws<IOException>(async () => { using var _ = await service.PrepareAsync(source, 0); });
        service.AfterSnapshot = null;
        using var good = await service.PrepareAsync(source, service.Revision(source.Path));
        True(good.Reused); Equal(1L, repository.CompilationCount);
    });
    await Case("A12-corrupt-pixels-regenerate-once-without-overwrite", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var first = await service.PrepareAsync(source, 0); var directory = first.Directory; first.Dispose();
        var path = Path.Combine(directory, "pixels.bin"); var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        using var repaired = await service.PrepareAsync(source, 0);
        Equal(2L, repository.CompilationCount); True(directory != repaired.Directory); True(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes));
    });
    foreach (var damage in new[] { "invalid-manifest-json", "truncated-pixels" })
    await Case("A12-" + damage + "-regenerates-once-and-then-reuses", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var first = await service.PrepareAsync(source, 0); var directory = first.Directory; first.Dispose();
        if (damage == "invalid-manifest-json") File.WriteAllText(Path.Combine(directory, "manifest.json"), "{");
        else using (var truncated = new FileStream(Path.Combine(directory, "pixels.bin"), FileMode.Open, FileAccess.Write)) truncated.SetLength(1);
        using var repaired = await service.PrepareAsync(source, 0);
        Equal(2L, repository.CompilationCount); True(directory != repaired.Directory);
        using var warm = await service.PrepareAsync(source, 0);
        True(warm.Reused); Equal(repaired.Directory, warm.Directory); Equal(2L, repository.CompilationCount);
        using var document = new CompiledDocument(warm.Directory); document.VerifyAll();
    });
    await Case("bounded-queue-and-canceled-cross-service-compiler-slot", async () =>
    {
        var a = Source(); var b = Source(seed: 72); var c = Source(seed: 73);
        var firstRepository = Repository(); var secondRepository = Repository();
        using var first = new SourcePreparationService(firstRepository, maxPending: 1);
        using var second = new SourcePreparationService(secondRepository);
        var entered = Signal(); var resume = Signal(); var secondEntries = 0;
        first.BeforeSnapshot = async token => { entered.SetResult(); await resume.Task.WaitAsync(token); };
        second.BeforeSnapshot = _ => { Interlocked.Increment(ref secondEntries); return Task.CompletedTask; };
        var active = first.PrepareAsync(a, 0); await entered.Task;
        try
        {
            await Throws<CacheCapacityException>(async () => { using var _ = await first.PrepareAsync(c, 0); });
            using var cancel = new CancellationTokenSource(); var queued = second.PrepareAsync(b, 0, cancel.Token);
            cancel.Cancel(); await Throws<OperationCanceledException>(async () => { using var _ = await queued; });
            Equal(0, secondEntries); Equal(0L, secondRepository.CompilationCount);
            True(!Directory.Exists(secondRepository.SnapshotRoot));
        }
        finally { resume.TrySetResult(); }
        using var completed = await active; Equal(1L, firstRepository.CompilationCount);
        using var next = await second.PrepareAsync(b, 0); Equal(1, secondEntries); Equal(1L, secondRepository.CompilationCount);
    });
    await Case("A13-cache-deleted-reconstruct-from-original", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var first = await service.PrepareAsync(source, 0); var directory = first.Directory; first.Dispose();
        repository.Trim(0); True(!Directory.Exists(directory));
        using var rebuilt = await service.PrepareAsync(source, 0); Equal(2L, repository.CompilationCount);
    });
    await Case("A15-competing-repositories-publish-one-generation", async () =>
    {
        var source = Source(); var repository = Repository();
        using var a = new SourcePreparationService(repository); using var b = new SourcePreparationService(repository);
        var leases = await Task.WhenAll(a.PrepareAsync(source, 0), b.PrepareAsync(source, 0));
        try { Equal(leases[0].Directory, leases[1].Directory); Equal(1L, repository.CompilationCount); }
        finally { foreach (var l in leases) l.Dispose(); }
    });
    await Case("A16-A18-generation-and-prepared-blocks-pin-through-trim", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var asset = await service.PrepareAsync(source, 0); var directory = asset.Directory;
        using var pool = new SharedDocumentPool(1024 * 1024, 1);
        using var ready = asset.PrepareAppearance(pool);
        repository.Trim(0); pool.Trim(); True(Directory.Exists(directory));
        Equal(1, pool.Snapshot().ActiveDocuments);
        foreach (var id in ready.Plan.RequiredBlockIds) True(ready.GetBlock(id).Length > 0);
        ready.Dispose(); repository.Trim(0); True(!Directory.Exists(directory)); Equal(0, pool.Snapshot().ActiveDocuments);
    });
    await Case("A19-missing-block-has-no-disk-fallback", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var asset = await service.PrepareAsync(source, 0); using var pool = new SharedDocumentPool(1024 * 1024, 2);
        using var ready = asset.PrepareAppearance(pool, []);
        await Throws<InvalidOperationException>(() => { ready.GetBlock(0); return Task.CompletedTask; });
        Equal(0L, pool.Snapshot().BlockReadCount);
    });
    await Case("A15-Windows-competing-processes-publish-one-generation", async () =>
    {
        var source = Source(); var cacheRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Process Launch()
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--prepare");
            foreach (var p in new[] { source.Path, cacheRoot }) start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(p)));
            return Process.Start(start)!;
        }
        using var a = Launch(); using var b = Launch();
        try
        {
            Equal("READY", await a.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Equal("READY", await b.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await a.StandardInput.WriteLineAsync(); await b.StandardInput.WriteLineAsync();
            var x = JsonDocument.Parse((await a.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            var y = JsonDocument.Parse((await b.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            Equal(x.RootElement.GetProperty("Directory").GetString(), y.RootElement.GetProperty("Directory").GetString());
            Equal(1L, x.RootElement.GetProperty("CompilationCount").GetInt64() + y.RootElement.GetProperty("CompilationCount").GetInt64());
            await Task.WhenAll(a.WaitForExitAsync(), b.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(5)); Equal(0, a.ExitCode); Equal(0, b.ExitCode);
        }
        finally { if (!a.HasExited) a.Kill(); if (!b.HasExited) b.Kill(); }
    });
    await Case("A16-Windows-other-process-read-pin-excludes-cleanup", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var asset = await service.PrepareAsync(source, 0); var directory = asset.Directory;
        var useFile = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(directory))!, "usage", Path.GetFileName(directory) + ".use");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--pin"); start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(useFile)));
        using var child = Process.Start(start)!;
        try
        {
            Equal("PINNED", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            asset.Dispose(); repository.Trim(0); True(Directory.Exists(directory));
            await child.StandardInput.WriteLineAsync(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, child.ExitCode); repository.Trim(0); True(!Directory.Exists(directory));
        }
        finally { if (!child.HasExited) child.Kill(); }
    });
    await Case("A14-disk-capacity-rejects-new-generation-preserves-source", async () =>
    {
        var source = Source(); var bytes = File.ReadAllBytes(source.Path);
        var repository = new CompiledAssetRepository(Path.Combine(root, Guid.NewGuid().ToString("N")), maximumBytes: 1);
        using var service = new SourcePreparationService(repository);
        await Throws<CacheCapacityException>(async () => { using var _ = await service.PrepareAsync(source, 0); });
        True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(source.Path))); Equal(0, Directory.GetDirectories(repository.SnapshotRoot).Length);
    });
    await Case("A14-A20-explicit-document-capacity-keeps-old-lease", async () =>
    {
        var a = Source(); var b = Source(seed: 99); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var pool = new SharedDocumentPool(1024 * 1024, 1);
        using var x = await service.PrepareAsync(a, 0); using var old = x.PrepareAppearance(pool);
        using var y = await service.PrepareAsync(b, 0);
        await Throws<CacheCapacityException>(() => { using var _ = y.PrepareAppearance(pool); return Task.CompletedTask; });
        True(old.GetBlock(old.Plan.RequiredBlockIds[0]).Length > 0); Equal(1, pool.Snapshot().ActiveDocuments);
    });
    await Case("A08-A10-A11-A30-session-stamps-dispose-late-candidates", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var asset = await service.PrepareAsync(source, 0); using var pool = new SharedDocumentPool(1024 * 1024, 2);
        using var session = new SourceSession(); var late = new TaskCompletionSource<PreparedAppearanceLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stampA = session.Request(0, 0, 0, (_, _) => late.Task);
        var stampB = session.Request(0, 1, 0, async (_, token) => { using var next = await service.PrepareAsync(source, 0, token); return next.PrepareAppearance(pool); });
        var stampLastA = session.Request(0, 2, 0, async (_, token) => { using var next = await service.PrepareAsync(source, 0, token); return next.PrepareAppearance(pool); });
        True(stampA != stampLastA); True(stampB != stampLastA);
        var lateReady = asset.PrepareAppearance(pool); late.SetResult(lateReady);
        await Until(() => session.State == PreparationState.ReadyToRender);
        using var ready = session.TakeReady(out var current); Equal(stampLastA, current); True(ready is not null);
        True(!session.TryPublish(stampA, () => throw new Exception("Stale published")));
        True(session.TryPublish(current, () => { }));
        session.ChangeDeviceEpoch(); True(!session.TryPublish(current, () => throw new Exception("Old device published")));
        session.Dispose(); await Throws<ObjectDisposedException>(() => { session.Request(0, 0, 0, (_, _) => late.Task); return Task.CompletedTask; });
        await Until(() => { try { lateReady.GetBlock(0); return false; } catch (ObjectDisposedException) { return true; } });
    });
    await Case("A11-clear-and-dispose-while-preparing-releases-late-lease", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var pool = new SharedDocumentPool(1024 * 1024, 2); using var asset = await service.PrepareAsync(source, 0);
        var ready = asset.PrepareAppearance(pool); var completion = new TaskCompletionSource<PreparedAppearanceLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new SourceSession(); var callbacks = 0; session.Ready += _ => callbacks++;
        session.Request(0, 0, 0, (_, _) => completion.Task); session.Clear(); session.Dispose(); completion.SetResult(ready);
        await Until(() => pool.Snapshot().ActiveDocuments == 0); Equal(0, callbacks); Equal(PreparationState.Disposed, session.State);
    });
    await Case("A17-cold-block-load-does-not-hold-cache-gate", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var asset = await service.PrepareAsync(source, 0); using var doc = new CompiledDocument(asset.Directory);
        var entered = Signal(); var resume = Signal();
        using var cache = new BlockCache(doc, 1024 * 1024, id =>
        { if (id == 1) { entered.SetResult(); resume.Task.GetAwaiter().GetResult(); } return doc.ReadBlock(id); });
        using var warm = cache.Acquire(0); var cold = Task.Run(() => cache.Acquire(1)); await entered.Task;
        try { using var again = await Task.Run(() => cache.Acquire(0)).WaitAsync(TimeSpan.FromSeconds(5)); True(again.Memory.Length > 0); cache.Trim(); }
        finally { resume.SetResult(); }
        using var loaded = await cold; Equal(2L, doc.BlockReadCount);
    });
    await Case("A22-warm-appearance-plan-does-not-reload", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var asset = await service.PrepareAsync(source, 0); using var pool = new SharedDocumentPool(1024 * 1024, 2);
        using var document = pool.Acquire(asset.Directory); var plan = RenderPlan.Create(document.Manifest);
        using var a = PreparedAppearanceLease.Prepare(document, plan); var reads = pool.Snapshot().BlockReadCount;
        using var b = PreparedAppearanceLease.Prepare(document, plan); Equal(reads, pool.Snapshot().BlockReadCount);
    });
    await Case("A17-slow-document-open-does-not-block-ready-document", async () =>
    {
        var sourceA = Source(); var sourceB = Source(seed: 77); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var a = await service.PrepareAsync(sourceA, 0); using var b = await service.PrepareAsync(sourceB, 0);
        using var pool = new SharedDocumentPool(1024 * 1024, 2); using var warm = pool.Acquire(a.Directory);
        var entered = Signal(); var resume = Signal();
        pool.BeforeOpen = path => { if (path == b.Directory) { entered.SetResult(); resume.Task.GetAwaiter().GetResult(); } };
        var cold = Task.Run(() => pool.Acquire(b.Directory)); await entered.Task;
        try { using var ready = await Task.Run(() => pool.Acquire(a.Directory)).WaitAsync(TimeSpan.FromSeconds(5)); Equal(1, pool.Snapshot().ActiveDocuments); }
        finally { resume.SetResult(); }
        using var loaded = await cold; Equal(2, pool.Snapshot().ActiveDocuments);
    });
    await Case("A21-watcher-hints-coalesce-and-overflow-marks-registered-paths", async () =>
    {
        var a = Source(); var b = Source(seed: 6); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var x = await service.PrepareAsync(a, 0); using var y = await service.PrepareAsync(b, 0);
        var notifications = 0; service.SourceChanged += (_, _) => notifications++;
        service.ManualHints = true;
        for (var i = 0; i < 100; i++) service.NotifyHint(a.Path);
        service.FlushHint(a.Path); Equal(1L, service.Revision(a.Path)); Equal(1, notifications);
        service.NotifyOverflow(); service.FlushHint(a.Path); service.FlushHint(b.Path);
        Equal(2L, service.Revision(a.Path)); Equal(1L, service.Revision(b.Path)); Equal(3, notifications);
        Equal(2L, repository.CompilationCount); // Hints mark dirty without eagerly compiling.
    });
    await Case("A07-cooperative-cancel-after-parser-layer-write-cleans-all-work", async () =>
    {
        var source = Source(); var original = File.ReadAllBytes(source.Path);
        var directory = Path.Combine(root, "cancel-during-compile"); using var cancel = new CancellationTokenSource();
        var compiler = new PsdCompiler(); var layers = 0; compiler.AfterLayerCompiled = () => { layers++; cancel.Cancel(); };
        await Throws<OperationCanceledException>(() => { compiler.Compile(source.Path, directory, cancel.Token); return Task.CompletedTask; });
        Equal(1, layers); Equal(0, Directory.GetDirectories(directory).Length);
        True(original.AsSpan().SequenceEqual(File.ReadAllBytes(source.Path)));
    });
    await Case("A09-one-settled-retry-updates-stamp-and-publishes-current-content", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var pool = new SharedDocumentPool(1024 * 1024, 2); using var session = new SourceSession();
        var attempts = 0; session.BeforeRetry = _ => Task.CompletedTask;
        service.AfterSnapshot = (_, _) =>
        {
            if (++attempts == 1) { PsdFixture.Write(source.Path, seed: 52); service.Invalidate(source.Path); }
            return Task.CompletedTask;
        };
        session.Request(0, 0, 0, async (stamp, token) =>
        { using var asset = await service.PrepareAsync(source, stamp.SourceRevision, token); return asset.PrepareAppearance(pool); },
            retryUnstable: true, readSourceRevision: () => service.Revision(source.Path));
        await session.Completion;
        Equal(2, attempts); Equal(1, session.AutomaticRetryCount); Equal(PreparationState.ReadyToRender, session.State);
        using var ready = session.TakeReady(out var final); Equal(1L, final.SourceRevision); True(ready is not null);
        True(!session.RetryChangedCandidate(final, 2, (_, _) => throw new Exception("Third attempt")));
        True(session.TryPublish(final, () => { })); Equal(0, Directory.GetDirectories(repository.SnapshotRoot).Length);
    });
    await Case("A09-continuously-changing-source-stops-after-one-retry", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var session = new SourceSession(); var attempts = 0; session.BeforeRetry = _ => Task.CompletedTask;
        service.AfterSnapshot = (_, _) =>
        { PsdFixture.Write(source.Path, seed: (byte)(40 + ++attempts)); service.Invalidate(source.Path); return Task.CompletedTask; };
        session.Request(0, 0, 0, async (stamp, token) =>
        { using var asset = await service.PrepareAsync(source, stamp.SourceRevision, token); throw new Exception("Unstable preparation unexpectedly succeeded"); },
            retryUnstable: true, readSourceRevision: () => service.Revision(source.Path));
        await session.Completion; Equal(2, attempts); Equal(1, session.AutomaticRetryCount);
        Equal(PreparationState.UnstableSource, session.State); Equal(PreparationRecovery.WaitForStableSource, session.Diagnostic!.Recovery);
        True(session.TakeReady(out _) is null); Equal(0, Directory.GetDirectories(repository.SnapshotRoot).Length);
    });
    await Case("A09-strict-request-and-access-failure-do-not-auto-retry", async () =>
    {
        foreach (var error in new Exception[] { new SourceChangedDuringPreparationException("changed"), new UnauthorizedAccessException("denied") })
        {
            using var session = new SourceSession(); var attempts = 0; session.BeforeRetry = _ => Task.CompletedTask;
            session.Request(0, 0, 0, (_, _) => { attempts++; throw error; }, retryUnstable: error is UnauthorizedAccessException);
            await session.Completion; Equal(1, attempts); Equal(0, session.AutomaticRetryCount);
            Equal(error is UnauthorizedAccessException ? PreparationRecovery.CheckAccess : PreparationRecovery.WaitForStableSource, session.Diagnostic!.Recovery);
        }
    });
    await Case("A09-post-ready-retry-shares-the-single-request-budget", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var pool = new SharedDocumentPool(1024 * 1024, 2); using var session = new SourceSession();
        async Task<PreparedAppearanceLease> Prepare(RequestStamp stamp, CancellationToken token)
        { using var asset = await service.PrepareAsync(source, stamp.SourceRevision, token); return asset.PrepareAppearance(pool); }
        session.Request(0, 0, 0, Prepare); await session.Completion; using var old = session.TakeReady(out var stamp);
        var revision = service.Invalidate(source.Path); True(session.RetryChangedCandidate(stamp, revision, Prepare));
        await session.Completion; using var current = session.TakeReady(out var next);
        Equal(1, session.AutomaticRetryCount); Equal(revision, next.SourceRevision); True(current is not null);
        True(!session.RetryChangedCandidate(next, revision + 1, (_, _) => throw new Exception("Third attempt")));
        True(!session.TryPublish(stamp, () => throw new Exception("Old request published")));
        True(session.TryPublish(next, () => { })); Equal(1L, repository.CompilationCount);
    });
    await Case("A09-Windows-original-sharing-conflict-retries-once-after-release", async () =>
    {
        var source = Source(); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var pool = new SharedDocumentPool(1024 * 1024, 1); using var session = new SourceSession();
        using var writer = new FileStream(source.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        session.BeforeRetry = _ => { writer.Dispose(); return Task.CompletedTask; }; var attempts = 0;
        session.Request(0, 0, 0, async (stamp, token) =>
        { attempts++; using var asset = await service.PrepareAsync(source, stamp.SourceRevision, token); return asset.PrepareAppearance(pool); }, retryUnstable: true);
        await session.Completion; Equal(2, attempts); Equal(1, session.AutomaticRetryCount);
        Equal(PreparationState.ReadyToRender, session.State); using var ready = session.TakeReady(out _); True(ready is not null);
    });
    await Case("A09-A14-Windows-persistent-source-lock-retains-previous-and-explicit-reload-recovers", async () =>
    {
        var source = Source(); var original = File.ReadAllBytes(source.Path); var repository = Repository();
        using var service = new SourcePreparationService(repository);
        using var pool = new SharedDocumentPool(1024 * 1024, 1); using var session = new SourceSession();
        var attempts = 0; var notifications = 0; var publications = 0;
        session.BeforeRetry = _ => Task.CompletedTask;
        session.Ready += _ => notifications++;
        async Task<PreparedAppearanceLease> Prepare(RequestStamp stamp, CancellationToken token)
        {
            attempts++;
            using var asset = await service.PrepareAsync(source, stamp.SourceRevision, token);
            return asset.PrepareAppearance(pool);
        }
        session.Request(0, 0, 0, Prepare); await session.Completion;
        using var previous = session.TakeReady(out var previousStamp); True(previous is not null);
        var blockId = previous!.Plan.RequiredBlockIds.First(); var pixels = previous.GetBlock(blockId).ToArray();
        True(session.TryPublish(previousStamp, () => publications++)); Equal(1, notifications);
        using (var writer = new FileStream(source.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Keep a real OS sharing conflict through both attempts, rather than throwing a synthetic exception.
            session.Request(0, 0, 0, Prepare, retryUnstable: true);
            await session.Completion;
            Equal(3, attempts); Equal(1, session.AutomaticRetryCount);
            Equal(PreparationState.UnstableSource, session.State);
            Equal(PreparationRecovery.WaitForStableSource, session.Diagnostic!.Recovery);
            True(session.Diagnostic.HasPreviousOutput);
            True(session.Error is SourceChangedDuringPreparationException { InnerException: IOException sharing }
                && (sharing.HResult & 0xffff) is 32 or 33);
            True(session.TakeReady(out _) is null); Equal(1, notifications); Equal(1, publications);
            Equal(1L, repository.CompilationCount); Equal(0, Directory.GetDirectories(repository.SnapshotRoot).Length);
            True(previous.GetBlock(blockId).Span.SequenceEqual(pixels));
            True(!session.TryPublish(previousStamp, () => throw new Exception("Stale request published")));
        }
        // Releasing the lock alone does not invent a third automatic attempt; this is an explicit new request.
        Equal(3, attempts);
        session.Request(0, 0, 0, Prepare); await session.Completion;
        Equal(4, attempts); Equal(0, session.AutomaticRetryCount); Equal(PreparationState.ReadyToRender, session.State);
        True(session.Error is null); True(session.Diagnostic is null);
        using var recovered = session.TakeReady(out var recoveredStamp); True(recovered is not null);
        True(recoveredStamp.AssetRequestId > previousStamp.AssetRequestId);
        True(recovered!.GetBlock(blockId).Span.SequenceEqual(pixels));
        True(previous.GetBlock(blockId).Span.SequenceEqual(pixels));
        True(session.TryPublish(recoveredStamp, () => publications++)); Equal(2, notifications); Equal(2, publications);
        Equal(PreparationState.DisplayedCurrent, session.State); Equal(1L, repository.CompilationCount);
        Equal(0, Directory.GetDirectories(repository.SnapshotRoot).Length);
        True(original.AsSpan().SequenceEqual(File.ReadAllBytes(source.Path)));
    });
    await Case("A11-clear-during-retry-settle-prevents-second-attempt", async () =>
    {
        using var session = new SourceSession(); var entered = Signal(); var canceled = Signal(); var attempts = 0;
        session.BeforeRetry = async token => { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { canceled.SetResult(); } };
        session.Request(0, 0, 0, (_, _) => { attempts++; throw new SourceChangedDuringPreparationException("changed"); }, retryUnstable: true);
        var completion = session.Completion; await entered.Task; session.Clear(); await canceled.Task; await completion;
        Equal(1, attempts); Equal(PreparationState.Empty, session.State); True(session.Error is null);
    });
    await Case("A14-write-admission-retains-old-pin-and-removes-failed-new-work", async () =>
    {
        var a = Source(); var b = Source(seed: 71); var repository = Repository(); using var service = new SourcePreparationService(repository);
        using var old = await service.PrepareAsync(a, 0);
        var oldBytes = Directory.GetFiles(old.Directory).Sum(f => new FileInfo(f).Length);
        var original = File.ReadAllBytes(b.Path);
        var limited = new CompiledAssetRepository(Path.GetDirectoryName(Path.GetDirectoryName(old.Directory))!,
            oldBytes + original.Length + CompiledFormat.HeaderSize + 1);
        using var blocked = new SourcePreparationService(limited);
        await Throws<CacheCapacityException>(async () => { using var _ = await blocked.PrepareAsync(b, 0); });
        using var intact = new CompiledDocument(old.Directory); intact.VerifyAll();
        True(original.AsSpan().SequenceEqual(File.ReadAllBytes(b.Path)));
        Equal(0, Directory.GetDirectories(limited.SnapshotRoot).Length);
        Equal(0, Directory.GetDirectories(Path.GetDirectoryName(old.Directory)!, ".tmp-*").Length);
        Equal(1, Directory.GetDirectories(Path.GetDirectoryName(old.Directory)!).Length);
    });
    await Case("A14-manifest-is-admitted-before-writing", () =>
    {
        var directory = Path.Combine(root, "writer-admission"); using var writer = new CompiledStoreWriter(directory, maximumOutputBytes: 44);
        var block = writer.AddBlock(BlockFormat.Bgra8Straight, 1, 1, new byte[] { 0, 0, 255, 255 }, compress: false);
        var fingerprint = new SourceFingerprint(CompiledFormat.Hash(new byte[26]), 26);
        var manifest = new CompiledManifest(1, CompiledFormat.CompilerId, CompiledFormat.Generation(fingerprint), fingerprint,
            1, 1, 1, 8, 3, null, null,
            System.Collections.Immutable.ImmutableArray.Create(new LayerNode(0, null, 0, NodeKind.Layer, "x", new(0,0,1,1), 0, true, "norm", 255, false, block, null, [])), writer.Blocks);
        return Throws<CacheCapacityException>(() =>
        { try { writer.Complete(directory, manifest); } finally { True(!File.Exists(Path.Combine(directory, "manifest.json"))); } return Task.CompletedTask; });
    });
    await Case("A14-owned-crash-work-cleanup-skips-live-pin-and-unrelated-file", async () =>
    {
        var source = Source(); var repository = Repository(); repository.Initialize();
        var abandoned = Path.Combine(repository.SnapshotRoot, ".snapshot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "source.psd"), "abandoned");
        var unrelated = Path.Combine(repository.SnapshotRoot, ".snapshot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
        using var live = new PsdCompiler().CreateSnapshot(source.Path, repository.SnapshotRoot);
        using var service = new SourcePreparationService(repository); using var asset = await service.PrepareAsync(source, 0);
        True(!Directory.Exists(abandoned)); True(File.Exists(live.Path)); True(File.Exists(Path.Combine(unrelated, "keep.txt")));
    });
    await Case("A21-resume-checks-only-active-registrations-once-per-path", async () =>
    {
        var a = Source(); var history = Source(seed: 81); var repository = Repository(); using var service = new SourcePreparationService(repository);
        service.ManualHints = true; using var historical = await service.PrepareAsync(history, 0);
        var reasons = new List<SourceChangeReason>(); service.SourceInvalidated += (_, _, reason) => reasons.Add(reason);
        using var x = service.Watch(a.Path); using var y = service.Watch(a.Path);
        service.RecheckWatchedSources(); Equal(1L, service.Revision(a.Path)); Equal(0L, service.Revision(history.Path));
        Equal(SourceChangeReason.Resume, reasons.Single()); x.Dispose(); y.Dispose(); service.RecheckWatchedSources(); Equal(1, reasons.Count);
    });
    await Case("A21-missing-parent-watch-rearms-on-resume", () =>
    {
        var path = Path.Combine(root, "new-parent", "source.psd"); using var service = new SourcePreparationService(Repository()); service.ManualHints = true;
        using var watch = service.Watch(path); True(service.MonitoringError(path) is not null);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); PsdFixture.Write(path); service.RecheckWatchedSources();
        True(service.MonitoringError(path) is null); Equal(1L, service.Revision(path)); return Task.CompletedTask;
    });
    await Case("A21-Windows-real-rename-dirties-old-and-new-registered-paths", async () =>
    {
        var old = Source(); var next = Path.Combine(root, Guid.NewGuid() + ".psd"); using var service = new SourcePreparationService(Repository());
        var oldHint = Signal(); var nextHint = Signal();
        service.SourceInvalidated += (path, _, reason) =>
        {
            if (reason != SourceChangeReason.ExternalHint) return;
            if (string.Equals(path, old.Path, StringComparison.OrdinalIgnoreCase)) oldHint.TrySetResult();
            if (string.Equals(path, next, StringComparison.OrdinalIgnoreCase)) nextHint.TrySetResult();
        };
        using var a = service.Watch(old.Path); using var b = service.Watch(next); File.Move(old.Path, next);
        await Task.WhenAll(oldHint.Task, nextHint.Task).WaitAsync(TimeSpan.FromSeconds(5));
        True(service.Revision(old.Path) > 0); True(service.Revision(next) > 0);
    });
    await Case("source-reference-schema-and-relink-keep-logical-identity", () =>
    {
        var source = Source(); var relocated = source.Relink("relocated.psd"); Equal(source.AssetIdentity, relocated.AssetIdentity);
        Equal(source, JsonSerializer.Deserialize<SourceAssetRef>(JsonSerializer.Serialize(source)));
        return Throws<NotSupportedException>(() => { (source with { SchemaVersion = 99 }).Validate(); return Task.CompletedTask; });
    });
}
finally { Directory.Delete(root, true); }
var summary = new { schema = "psd-tachie-next.preparation-tests.v1", total = results.Count, failed, assertions,
    sourceHead = Environment.GetEnvironmentVariable("GITHUB_SHA"), runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, prefetchMeasurements, results };
File.WriteAllText(args[0], JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PREPARATION SUMMARY: {results.Count-failed}/{results.Count}; {assertions} assertions; {failed} failures.");
return failed == 0 ? 0 : 1;
TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
SourceAssetRef Source(byte seed = 5) { var p = Path.Combine(root, Guid.NewGuid() + ".psd"); PsdFixture.Write(p, seed: seed); return SourceAssetRef.Create(p); }
CompiledAssetRepository Repository() => new(Path.Combine(root, "cache-" + Guid.NewGuid().ToString("N")));
async Task Case(string name, Func<Task> body)
{
    try { await body().WaitAsync(TimeSpan.FromSeconds(30)); results.Add(new { name, status = "PASS" }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new { name, status = "FAIL", error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e); }
}
void True(bool value) { assertions++; if (!value) throw new Exception("Assertion failed."); }
void Equal<T>(T a, T b) { assertions++; if (!EqualityComparer<T>.Default.Equals(a, b)) throw new Exception($"Expected {a}; got {b}."); }
async Task Throws<T>(Func<Task> body) where T : Exception { assertions++; try { await body(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
async Task Until(Func<bool> predicate) { using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) { cancel.Token.ThrowIfCancellationRequested(); await Task.Yield(); } }
