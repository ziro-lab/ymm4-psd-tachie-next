using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;

internal static class SparsePreparationCases
{
    internal static async Task Run(string root,Func<string,Func<Task>,Task> test,Action<bool> check)
    {
        await test("C-sparse-original-PSD-PSB-layer-ID-cache-free-save-and-preparation",async()=>
        {
            foreach(var psb in new[]{false,true})
            {
                var path=Path.Combine(root,Guid.NewGuid()+(psb?".psb":".psd"));
                PsdFixture.Write(path,psb:psb,visibleName:"*base",hiddenName:"*other",layerIds:[11,12]);
                var bytes=File.ReadAllBytes(path);var source=SourceAssetRef.Create(path);
                var repository=new CompiledAssetRepository(Path.Combine(root,Guid.NewGuid().ToString("N")));
                using var service=new SourcePreparationService(repository);
                PsdAppearanceSettings settings;
                using(var pool=new SharedDocumentPool(4096,1))
                using(var asset=await service.PrepareAsync(source,0))
                using(var doc=pool.Acquire(asset.Directory))
                {
                    var index=PsdLayerReferenceIndex.Read(doc);
                    check(index.Capture(0).LayerId==11 && index.Capture(1).LayerId==12);
                    check(pool.Snapshot().BlockReadCount==2 && pool.Snapshot().ResidentDecodedBytes==8);
                    var edit=PsdAppearanceSettings.Create(source.AssetIdentity).SetVisible(source.AssetIdentity,index,1,true);
                    check(edit.Succeeded);settings=edit.Settings!.WithFlip(source.AssetIdentity,index,PsdFlipState.XY).Settings!;
                }
                var jsonPath=Path.Combine(root,Guid.NewGuid()+".json");File.WriteAllText(jsonPath,settings.Json);
                using(var pool=new SharedDocumentPool(4096,1))
                using(var asset=await service.PrepareAsync(source,0))
                using(var ready=asset.PrepareSavedAppearance(pool,source.AssetIdentity,PsdAppearanceSettings.FromJson(File.ReadAllText(jsonPath))))
                {
                    check(ready.Plan.ActiveNodeIds.SequenceEqual([1]) && ready.Plan.FlipState==PsdFlipState.XY);
                    check(ready.GetBlock(ready.Plan.RequiredBlockIds.Single()).Span.SequenceEqual(PsdFixture.Pixels(23)));
                    check(pool.Snapshot().BlockReadCount==3 && repository.CompilationCount==1);
                }
                // Entirely empty cache and new preparation owner: saved refs must not need
                // the original generation, node IDs, pool, decoded blocks or cache path.
                var freshRepository=new CompiledAssetRepository(Path.Combine(root,Guid.NewGuid().ToString("N")));
                using(var freshService=new SourcePreparationService(freshRepository))
                using(var pool=new SharedDocumentPool(4096,1))
                using(var asset=await freshService.PrepareAsync(source,0))
                using(var ready=asset.PrepareSavedAppearance(pool,source.AssetIdentity,PsdAppearanceSettings.FromJson(File.ReadAllText(jsonPath))))
                {
                    check(ready.Plan.ActiveNodeIds.SequenceEqual([1]) && ready.Plan.FlipState==PsdFlipState.XY);
                    check(freshRepository.CompilationCount==1 && ready.GetBlock(ready.Plan.RequiredBlockIds.Single()).Span.SequenceEqual(PsdFixture.Pixels(23)));
                    check(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
                }
                PsdFixture.Write(path,psb:psb,visibleName:"*base",hiddenName:"*renamed",layerIds:[11,12]);
                var revision=service.Invalidate(path);
                using(var pool=new SharedDocumentPool(4096,1))
                using(var asset=await service.PrepareAsync(source,revision))
                using(var ready=asset.PrepareSavedAppearance(pool,source.AssetIdentity,settings))
                    check(ready.Plan.ActiveNodeIds.SequenceEqual([1]) && repository.CompilationCount==2);
                check(!bytes.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
            }
        });
        await test("C-sparse-unresolved-unknown-and-hidden-notation-fail-before-pixel-decode-with-intent-preserved",async()=>
        {
            var path=Path.Combine(root,Guid.NewGuid()+".psd");PsdFixture.Write(path,visibleName:"*base",hiddenName:"*other",layerIds:[7,7]);
            var source=SourceAssetRef.Create(path);var repository=new CompiledAssetRepository(Path.Combine(root,Guid.NewGuid().ToString("N")));
            using var service=new SourcePreparationService(repository);PsdAppearanceSettings saved;
            using(var pool=new SharedDocumentPool(4096,1))
            using(var asset=await service.PrepareAsync(source,0))
            using(var doc=pool.Acquire(asset.Directory))
            {
                var index=PsdLayerReferenceIndex.Read(doc);check(index.Capture(1).LayerId is null);
                saved=PsdAppearanceSettings.Create(source.AssetIdentity).SetVisible(source.AssetIdentity,index,1,true).Settings!;
            }
            var before=saved.Json;
            PsdFixture.Write(path,seed:19,visibleName:"*base",hiddenName:"*other",layerIds:[7,7]);
            var revision=service.Invalidate(path);
            using(var pool=new SharedDocumentPool(4096,1))
            using(var asset=await service.PrepareAsync(source,revision))
            using(var doc=pool.Acquire(asset.Directory))
            {
                var result=saved.Resolve(source.AssetIdentity,PsdLayerReferenceIndex.Read(doc));
                check(!result.Succeeded && result.Repairs[0].Candidates.SequenceEqual([1]));
                PsdAppearanceRepairException? error=null;
                try{using var bad=asset.PrepareSavedAppearance(pool,source.AssetIdentity,saved);}catch(PsdAppearanceRepairException ex){error=ex;}
                check(error is not null && ReferenceEquals(error.Resolution.Settings,saved) && saved.Json==before);
                check(PreparationDiagnostic.From(error)!.Recovery==PreparationRecovery.RepairAppearance);
                check(pool.Snapshot().BlockReadCount==2 && pool.Snapshot().ResidentDecodedBytes==8);
            }
            foreach(var settings in new[]{PsdAppearanceSettings.FromJson("{\"version\":999,\"future\":[1,2]}")})
            using(var pool=new SharedDocumentPool(4096,1))
            using(var asset=await service.PrepareAsync(source,revision))
            using(var doc=pool.Acquire(asset.Directory))
            {
                PsdAppearanceRepairException? error=null;
                try{using var bad=asset.PrepareSavedAppearance(pool,source.AssetIdentity,settings);}catch(PsdAppearanceRepairException ex){error=ex;}
                check(error is not null && ReferenceEquals(error.Resolution.Settings,settings));
                check(pool.Snapshot().BlockReadCount==2);
            }
            PsdFixture.Write(path,visibleName:"plain",hiddenName:"flipx",layerIds:[11,12]);revision=service.Invalidate(path);
            using(var pool=new SharedDocumentPool(4096,1))
            using(var asset=await service.PrepareAsync(source,revision))
            using(var doc=pool.Acquire(asset.Directory))
            {
                var settings=PsdAppearanceSettings.Create(source.AssetIdentity);PsdAppearanceRepairException? error=null;
                try{using var bad=asset.PrepareSavedAppearance(pool,source.AssetIdentity,settings);}catch(PsdAppearanceRepairException ex){error=ex;}
                check(error is not null && ReferenceEquals(error.Resolution.Settings,settings));
                check(pool.Snapshot().BlockReadCount==2);
            }
        });
    }
}
