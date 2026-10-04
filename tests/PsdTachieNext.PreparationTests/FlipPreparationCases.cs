using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;

internal static class FlipPreparationCases
{
    internal static async Task Run(string root,Func<string,Func<Task>,Task> test,Action<bool> check)
    {
        SourceAssetRef Source(string normal,string variant)
        {
            var path=Path.Combine(root,Guid.NewGuid()+".psd");
            PsdFixture.Write(path,seed:17,visibleName:normal,hiddenName:variant);return SourceAssetRef.Create(path);
        }
        CompiledAssetRepository Repository()=>new(Path.Combine(root,"flip-"+Guid.NewGuid().ToString("N")));
        await test("C-original-flip-appearance-prepare-and-prefetch-share-notation-defaults",async()=>
        {
            var source=Source("*body","*body:flipx");var input=File.ReadAllBytes(source.Path);var repository=Repository();
            using var service=new SourcePreparationService(repository);using var pool=new SharedDocumentPool(4096,1);
            PsdVisibilityState state;
            using(var asset=await service.PrepareAsync(source,0))
            using(var normal=asset.PrepareNotationAppearance(pool))
            {
                state=normal.Plan.Visibility!;check(state.Bindings.Pairs.Length==1 && state.Bindings.Diagnostics.IsEmpty);
                check(normal.Plan.ActiveNodeIds.SequenceEqual([0]) && normal.Plan.FlipState==PsdFlipState.None);
                check(normal.GetBlock(normal.Plan.RequiredBlockIds.Single()).Span.SequenceEqual(PsdFixture.Pixels(17)));
            }
            using(var asset=await service.PrepareAsync(source,0))
            using(var flipped=asset.PrepareNotationAppearance(pool,state.WithFlip(PsdFlipState.X)))
            {
                check(flipped.Plan.ActiveNodeIds.SequenceEqual([1]) && flipped.Plan.FlipState==PsdFlipState.X);
                check(flipped.GetBlock(flipped.Plan.RequiredBlockIds.Single()).Span.SequenceEqual(PsdFixture.Pixels(23)));
                check(repository.CompilationCount==1);
            }
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),128);
            await prefetch.Select(source);using(var promoted=await prefetch.TakeVerifiedReadyAsync(source,0))
            {
                check(promoted?.Plan.Visibility is not null && promoted.Plan.ActiveNodeIds.SequenceEqual([0]));
                check(promoted!.Plan.Visibility!.Bindings.Diagnostics.IsEmpty);
            }
            check(repository.CompilationCount==1 && pool.Snapshot().ResidentDecodedBytes==0);
            check(input.AsSpan().SequenceEqual(File.ReadAllBytes(source.Path)));
        });
        await test("C-original-missing-flip-base-retains-literal-selection-and-warning-through-prefetch",async()=>
        {
            var source=Source("ordinary","body:flipx");var repository=Repository();
            using var service=new SourcePreparationService(repository);using var pool=new SharedDocumentPool(4096,1);
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),256);
            await prefetch.Select(source);check(prefetch.Snapshot().Ready);
            PsdVisibilityState state;
            using(var ready=await prefetch.TakeVerifiedReadyAsync(source,0))
            {
                state=ready!.Plan.Visibility!;
                check(state.Bindings.Diagnostics.Single().Issue==PsdFlipBindingIssue.MissingBase);
                check(!state.Bindings.Diagnostics.Single().BlocksPreparation && ready.Plan.ActiveNodeIds.SequenceEqual([0]));
            }
            using(var asset=await service.PrepareAsync(source,0))
            using(var ready=asset.PrepareNotationAppearanceWithinBudget(pool,256,state.SetVisible(1,true).WithFlip(PsdFlipState.X)))
            {
                check(ready.Plan.ActiveNodeIds.SequenceEqual([0,1]) && ready.Plan.FlipState==PsdFlipState.X);
                check(ready.Plan.Visibility!.Bindings.Diagnostics.Single().NodeId==1);
                check(ready.Plan.RequiredBlockIds.Sum(id=>ready.Plan.Manifest.Blocks[id].RawLength)==256);
            }
            check(repository.CompilationCount==1 && pool.Snapshot().ResidentDecodedBytes==0);
        });
        await test("C-original-direction-only-force-prefetch-None-and-X-use-required-blocks",async()=>
        {
            var source=Source("ordinary","!cape:flipx");var input=File.ReadAllBytes(source.Path);var repository=Repository();
            using var service=new SourcePreparationService(repository);using var pool=new SharedDocumentPool(4096,1);
            using var prefetch=new SelectedSourcePrefetch(service,pool,TimeSpan.FromSeconds(30),256);
            await prefetch.Select(source);PsdVisibilityState state;
            using(var normal=await prefetch.TakeVerifiedReadyAsync(source,0))
            {
                state=normal!.Plan.Visibility!;
                check(state.IsLocallyVisible(1) && state.Bindings.Selection.DirectionOnlyScopes[1]==PsdDirectionScope.X);
                check(normal.Plan.ActiveNodeIds.SequenceEqual([0]) && normal.Plan.RequiredBlockIds.Length==1);
                check(normal.GetBlock(normal.Plan.RequiredBlockIds.Single()).Span.SequenceEqual(PsdFixture.Pixels(17)));
            }
            foreach(var flip in new[]{PsdFlipState.X,PsdFlipState.None,PsdFlipState.X})
            {
                state=state.WithFlip(flip);
                using var asset=await service.PrepareAsync(source,0);using var ready=asset.PrepareNotationAppearance(pool,state);
                check(ready.Plan.ActiveNodeIds.SequenceEqual(flip==PsdFlipState.X ? new[]{0,1} : new[]{0}));
                check(state.IsLocallyVisible(1) && ReferenceEquals(state,state.SetVisible(1,false)));
                if(flip==PsdFlipState.X) check(ready.GetBlock(ready.Plan.RequiredBlockIds.Last()).Span.SequenceEqual(PsdFixture.Pixels(23)));
            }
            check(repository.CompilationCount==1 && pool.Snapshot().ResidentDecodedBytes==0);
            check(input.AsSpan().SequenceEqual(File.ReadAllBytes(source.Path)));
        });
    }
}
