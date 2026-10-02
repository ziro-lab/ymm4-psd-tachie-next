using System.Diagnostics;
using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;

internal static class SelectedPrefetchCases
{
    internal static async Task<object[]> Run(string root,Func<string,Func<Task>,Task> test,Action<bool> check)
    {
        var measurements=new List<object>();
        SourceAssetRef Source(byte seed){var path=Path.Combine(root,Guid.NewGuid()+".psd");PsdFixture.Write(path,seed:seed);return SourceAssetRef.Create(path);}
        CompiledAssetRepository Repository()=>new(Path.Combine(root,"prefetch-"+Guid.NewGuid().ToString("N")));
        static long Bytes(SharedDocumentPool pool)=>pool.Snapshot().ResidentDecodedBytes;
        static bool Pixels(PreparedAppearanceLease ready,byte seed)=>ready.GetBlock(ready.Plan.RequiredBlockIds.Single()).Span.SequenceEqual(PsdFixture.Pixels(seed));
        await test("selected-prefetch-ready-promotion-keeps-real-lease-through-expiry-and-disposal",async()=>
        {
            var source=Source(17);var repository=Repository();using var service=new SourcePreparationService(repository);
            using var pool=new SharedDocumentPool(4096,2);var clock=new Clock();
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),2048,clock);
            var watch=Stopwatch.StartNew();await prefetch.Select(source);watch.Stop();
            check(prefetch.Snapshot().Ready);check(Bytes(pool)==128);check(repository.CompilationCount==1);
            var independentIdentity=SourceAssetRef.Create(source.Path);await prefetch.Select(independentIdentity);
            check(prefetch.Snapshot().Started==1);check(independentIdentity.AssetIdentity!=source.AssetIdentity);
            var real=await prefetch.TakeVerifiedReadyAsync(independentIdentity,0);check(real is not null);
            using(real!)
            {
                check(Pixels(real!,17));clock.Advance(TimeSpan.FromSeconds(31));await prefetch.ExpireUnusedAsync();
                prefetch.Dispose();await prefetch.ShutdownCompletion;
                check(Bytes(pool)==128);check(Pixels(real!,17));check(prefetch.Snapshot().Promoted==1);
            }
            check(Bytes(pool)==0);check(pool.Snapshot().ActiveDocuments==0);
            measurements.Add(new {caseName="promotion",coldPreparationMs=watch.Elapsed.TotalMilliseconds,retainedBeforePromotionBytes=128,afterFinalRealReleaseBytes=Bytes(pool)});
        });
        await test("selected-prefetch-unused-expiry-frees-memory-keeps-disk-and-reselects",async()=>
        {
            var source=Source(21);var repository=Repository();using var service=new SourcePreparationService(repository);
            using var pool=new SharedDocumentPool(4096,2);var clock=new Clock();
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),2048,clock);
            await prefetch.Select(source);check(prefetch.Snapshot().Ready);check(Bytes(pool)==128);
            using var disk=await service.PrepareAsync(source,0);var directory=disk.Directory;
            clock.Advance(TimeSpan.FromSeconds(29));await prefetch.ExpireUnusedAsync();check(prefetch.Snapshot().Ready);
            clock.Advance(TimeSpan.FromSeconds(2));await prefetch.ExpireUnusedAsync();
            check(!prefetch.Snapshot().Ready);check(Bytes(pool)==0);check(Directory.Exists(directory));
            await prefetch.Select(source);check(prefetch.Snapshot().Ready);check(repository.CompilationCount==1);check(prefetch.Snapshot().Started==2);
            await prefetch.Select(null);await prefetch.ShutdownCompletion;check(Bytes(pool)==0);check(Directory.Exists(directory));
            measurements.Add(new {caseName="expiry",logicalTtlSeconds=30,beforeBytes=128,afterExpiryBytes=Bytes(pool),persistentGenerationPreserved=Directory.Exists(directory)});
        });
        await test("selected-prefetch-rapid-switch-is-nonblocking-latest-only-and-cancels-stale",async()=>
        {
            var a=Source(27);var b=Source(31);var repository=Repository();using var service=new SourcePreparationService(repository);
            using var pool=new SharedDocumentPool(4096,2);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.BeforeSnapshot=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),2048);
            _=prefetch.Select(a);await entered.Task;
            var watch=Stopwatch.StartNew();Task latest=Task.CompletedTask;
            for(var i=0;i<128;i++)latest=prefetch.Select(i%2==0?a:b);
            watch.Stop();check(!release.Task.IsCompleted);check(prefetch.Snapshot().SelectedPath==b.Path);check(Bytes(pool)==0);
            release.TrySetResult();await latest;check(prefetch.Snapshot().Ready);check(repository.CompilationCount==1);
            check(prefetch.TryTakeReady(b,0,out var real));using(real!)check(Pixels(real!,31));
            prefetch.Dispose();await prefetch.ShutdownCompletion;check(Bytes(pool)==0);
            measurements.Add(new {caseName="rapid-switch",selectionCalls=128,selectionLoopMs=watch.Elapsed.TotalMilliseconds,compiledSources=repository.CompilationCount});
        });
        await test("selected-prefetch-actual-admission-priority-and-shared-job-survive-cancel",async()=>
        {
            var a=Source(41);var b=Source(43);var repository=Repository();using var service=new SourcePreparationService(repository);
            using var pool=new SharedDocumentPool(4096,1);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.BeforeSnapshot=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),2048);
            _=prefetch.Select(a);await entered.Task;
            // Enroll the actual waiter before cancellation to exercise the shared-worker boundary.
            var actual=service.PrepareAsync(a,0);using var actualUse=prefetch.BeginActualUse();
            await prefetch.Select(b);await prefetch.Select(null);check(prefetch.Snapshot().Started==1);
            await actualUse.Retirement;check(!release.Task.IsCompleted);
            release.TrySetResult();using var asset=await actual;using var real=asset.PrepareAppearance(pool);
            check(Pixels(real,41));check(repository.CompilationCount==1);check(Bytes(pool)==128);
            prefetch.Dispose();await prefetch.ShutdownCompletion;check(Pixels(real,41));
        });
        await test("selected-prefetch-budget-skips-decoding-without-breaking-real-use",async()=>
        {
            var source=Source(47);var repository=Repository();using var service=new SourcePreparationService(repository);
            using var pool=new SharedDocumentPool(4096,1);
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),127);
            await prefetch.Select(source);check(!prefetch.Snapshot().Ready);check(prefetch.Snapshot().Error?.Contains("budget exceeded")==true);
            check(Bytes(pool)==0);check(pool.Snapshot().ActiveDocuments==0);
            using var actualUse=prefetch.BeginActualUse();using var asset=await service.PrepareAsync(source,0);
            await actualUse.Retirement;using var real=asset.PrepareAppearance(pool);
            check(Pixels(real,47));check(repository.CompilationCount==1);
        });
        await test("selected-prefetch-source-revision-change-rejects-stale-ready",async()=>
        {
            var source=Source(53);var repository=Repository();using var service=new SourcePreparationService(repository);service.ManualHints=true;
            using var pool=new SharedDocumentPool(4096,2);
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),2048);
            await prefetch.Select(source);check(prefetch.Snapshot().Ready);
            service.Invalidate(source.Path);await prefetch.ShutdownCompletion;
            check(!prefetch.TryTakeReady(source,0,out _));check(Bytes(pool)==0);
            PsdFixture.Write(source.Path,seed:59);var revision=service.Invalidate(source.Path);
            await prefetch.Select(source);check(prefetch.TryTakeReady(source,revision,out var real));
            using(real!)check(Pixels(real!,59));check(repository.CompilationCount==2);
        });
        await test("selected-prefetch-selection-during-real-use-coalesces-until-admission-ends",async()=>
        {
            var a=Source(61);var b=Source(67);using var service=new SourcePreparationService(Repository());
            using var pool=new SharedDocumentPool(4096,2);
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),2048);
            using(var actualUse=prefetch.BeginActualUse())
            {
                for(var i=0;i<32;i++)await prefetch.Select(i%2==0?a:b);
                check(prefetch.Snapshot().Started==0);check(Bytes(pool)==0);
            }
            while(!prefetch.Snapshot().Ready){await Task.Delay(1);if(prefetch.Snapshot().Error is { } error)throw new Exception(error);}
            check(prefetch.Snapshot().Started==1);check(prefetch.Snapshot().SelectedPath==b.Path);
            check(prefetch.TryTakeReady(b,0,out var real));using(real!)check(Pixels(real!,67));
        });
        await test("selected-prefetch-dispose-cancels-blocked-work-without-waiting-on-selection",async()=>
        {
            var source=Source(71);var repository=Repository();using var service=new SourcePreparationService(repository);
            using var pool=new SharedDocumentPool(4096,1);
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.BeforeSnapshot=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),2048);
            _=prefetch.Select(source);await entered.Task;
            prefetch.Dispose();check(!release.Task.IsCompleted);await prefetch.ShutdownCompletion;
            check(!prefetch.Snapshot().Ready);check(Bytes(pool)==0);check(repository.CompilationCount==0);
            check(!prefetch.TryTakeReady(source,0,out _));release.TrySetResult();
        });
        await test("selected-prefetch-access-failure-is-observed-and-does-not-create-ready",async()=>
        {
            var source=Source(73);File.Delete(source.Path);using var service=new SourcePreparationService(Repository());
            using var pool=new SharedDocumentPool(4096,1);
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),2048);
            await prefetch.Select(source);check(!prefetch.Snapshot().Ready);check(prefetch.Snapshot().Error is not null);
            check(Bytes(pool)==0);check(!prefetch.TryTakeReady(source,0,out _));
        });
        await test("selected-prefetch-promotion-verifies-same-size-mtime-bytes-before-real-use",async()=>
        {
            var source=Source(79);var repository=Repository();using var service=new SourcePreparationService(repository);service.ManualHints=true;
            using var pool=new SharedDocumentPool(4096,2);
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),2048);
            await prefetch.Select(source);var length=new FileInfo(source.Path).Length;var time=File.GetLastWriteTimeUtc(source.Path);
            PsdFixture.Write(source.Path,seed:83);File.SetLastWriteTimeUtc(source.Path,time);
            check(new FileInfo(source.Path).Length==length);check(service.Revision(source.Path)==0);
            try{using var stale=await prefetch.TakeVerifiedReadyAsync(source,0);check(false);}
            catch(SourceChangedDuringPreparationException){check(true);}
            await prefetch.ShutdownCompletion;check(Bytes(pool)==0);check(service.Revision(source.Path)>0);
            await prefetch.Select(source);using var current=await prefetch.TakeVerifiedReadyAsync(source,service.Revision(source.Path));
            check(current is not null&&Pixels(current,83));check(repository.CompilationCount==2);
        });
        return measurements.ToArray();
    }
    private sealed class Clock:TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan value)=>Interlocked.Add(ref timestamp,value.Ticks);
    }
}
