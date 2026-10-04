using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;

internal static class PrefixPreparationCases
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool> check)
    {
        SourceAssetRef Source(string front, string back)
        {
            var path = Path.Combine(root, Guid.NewGuid() + ".psd");
            PsdFixture.Write(path, seed: 17, visibleName: front, hiddenName: back);
            return SourceAssetRef.Create(path);
        }
        CompiledAssetRepository Repository() => new(Path.Combine(root, "prefix-" + Guid.NewGuid().ToString("N")));

        await test("C-original-prefix-switch-reuses-compile-and-prepares-requested-pixels", async () =>
        {
            var source = Source("*front", "*back"); var input = File.ReadAllBytes(source.Path);
            var repository = Repository(); using var service = new SourcePreparationService(repository);
            using var pool = new SharedDocumentPool(4096, 1);
            PsdPrefixVisibility switched;
            using (var asset = await service.PrepareAsync(source, 0))
            using (var first = asset.PreparePrefixAppearance(pool))
            {
                check(first.Plan.ActiveNodeIds.SequenceEqual([0]));
                check(first.GetBlock(first.Plan.RequiredBlockIds.Single()).Span.SequenceEqual(PsdFixture.Pixels(17)));
                switched = first.Plan.PrefixVisibility!.SetVisible(1, true);
                check(first.Plan.PrefixVisibility.IsLocallyVisible(0));
            }
            using (var asset = await service.PrepareAsync(source, 0))
            using (var second = asset.PreparePrefixAppearance(pool, switched))
            {
                check(second.Plan.ActiveNodeIds.SequenceEqual([1]));
                check(second.GetBlock(second.Plan.RequiredBlockIds.Single()).Span.SequenceEqual(PsdFixture.Pixels(23)));
                check(ReferenceEquals(switched, second.Plan.PrefixVisibility));
                check(repository.CompilationCount == 1 && pool.Snapshot().ResidentDecodedBytes == 128);
            }
            check(pool.Snapshot().ActiveDocuments == 0 && pool.Snapshot().ResidentDecodedBytes == 0);
            check(input.AsSpan().SequenceEqual(File.ReadAllBytes(source.Path)));
        });
        await test("C-selected-prefetch-and-real-prefix-defaults-agree-on-forced-hidden-layer", async () =>
        {
            var source = Source("ordinary", "!forced"); var repository = Repository();
            using var service = new SourcePreparationService(repository); using var pool = new SharedDocumentPool(4096, 1);
            using var prefetch = new SelectedSourcePrefetch(service, pool, TimeSpan.FromSeconds(30), 256);
            await prefetch.Select(source); check(prefetch.Snapshot().Ready);
            using (var promoted = await prefetch.TakeVerifiedReadyAsync(source, 0))
            {
                check(promoted is not null && promoted.Plan.Visibility is not null);
                check(promoted!.Plan.ActiveNodeIds.SequenceEqual([0, 1]));
                check(promoted.Plan.RequiredBlockIds.Sum(id => promoted.Plan.Manifest.Blocks[id].RawLength) == 256);
            }
            using (var asset = await service.PrepareAsync(source, 0))
            using (var real = asset.PreparePrefixAppearance(pool))
                check(real.Plan.ActiveNodeIds.SequenceEqual([0, 1]));
            check(repository.CompilationCount == 1 && pool.Snapshot().ResidentDecodedBytes == 0);
        });
        await test("C-prefix-profile-rejection-cleans-leases-before-compiled-block-decode-and-preserves-raw-diagnostic", async () =>
        {
            var source = Source("flipx", "hidden"); var repository = Repository();
            using var service = new SourcePreparationService(repository); using var pool = new SharedDocumentPool(4096, 1);
            using (var asset = await service.PrepareAsync(source, 0))
            {
                var rejected = false;
                try { using var unexpected = asset.PreparePrefixAppearance(pool); }
                catch (UnsupportedPsdNotationException ex)
                {
                    rejected = ex.Reasons == PsdNotationUnsupportedReason.TokenOnlyName;
                    check(PreparationDiagnostic.From(ex)?.Recovery == PreparationRecovery.UnsupportedNotation);
                }
                check(rejected && pool.Snapshot().ActiveDocuments == 0 && pool.Snapshot().ResidentDecodedBytes == 0);
                check(repository.CompilationCount == 1); // Initial PSD pixel conversion has already occurred.
            }
            using (var asset = await service.PrepareAsync(source, 0))
            using (var raw = asset.PrepareAppearance(pool))
                check(raw.Plan.PrefixVisibility is null && raw.Plan.ActiveNodeIds.SequenceEqual([0]));
            using var prefetch = new SelectedSourcePrefetch(service, pool, TimeSpan.FromSeconds(30), 256);
            await prefetch.Select(source);
            check(!prefetch.Snapshot().Ready && prefetch.Snapshot().Error?.Contains("トークンだけの名前") == true);
            check(pool.Snapshot().ActiveDocuments == 0 && pool.Snapshot().ResidentDecodedBytes == 0);
        });
        await test("C-original-hidden-unsupported-notation-reaches-session-diagnostic-without-RGB8-guidance", async () =>
        {
            foreach (var name in new[] { "flipxy", "flipx:flipy" })
            {
                var source = Source("ordinary", name); var repository = Repository();
                using var service = new SourcePreparationService(repository); using var pool = new SharedDocumentPool(4096, 1);
                using var session = new SourceSession();
                session.Request(0, 0, 0, async (stamp, token) =>
                {
                    using var asset = await service.PrepareAsync(source, stamp.SourceRevision, token);
                    return await Task.Run(() => asset.PrepareNotationAppearance(pool, token: token), token);
                }, retryUnstable: true, readSourceRevision: () => 0);
                await session.Completion;
                check(session.State == PreparationState.Failed && session.Error is UnsupportedPsdNotationException);
                var diagnostic = session.Diagnostic;
                check(diagnostic?.Recovery == PreparationRecovery.UnsupportedNotation && !diagnostic.HasPreviousOutput);
                check(!diagnostic!.Message.Contains("RGB8") && !diagnostic.Action.Contains("RGB8"));
                if (name.StartsWith("flip")) check(diagnostic.Message.Contains("トークンだけの名前"));
                check(repository.CompilationCount == 1 && session.AutomaticRetryCount == 0);
                check(pool.Snapshot().ActiveDocuments == 0 && pool.Snapshot().ResidentDecodedBytes == 0);
                check(session.TakeReady(out _) is null);
            }
        });
    }
}
