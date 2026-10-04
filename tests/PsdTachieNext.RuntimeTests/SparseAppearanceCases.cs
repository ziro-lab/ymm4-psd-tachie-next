using System.Text.Json.Nodes;
using PsdTachieNext.Core;
using Spec = FlipCases.Spec;

internal static class SparseAppearanceCases
{
    internal static void Run(Action<string,Action> test,Action<bool> check,string root)
    {
        test("C-sparse-unowned-new-defaults-explicit-off-and-same-default-selection-survive-ID-recovery",()=>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var old=pool.Acquire(FlipCases.Store(root,new(null,"authored",false,PsdId:10),new(null,"inherited",false,PsdId:20)));
            var index=PsdLayerReferenceIndex.Read(old);
            var settings=PsdAppearanceSettings.Create("asset");
            check(PsdAppearanceSettings.ResolveOrDefault(null,"asset",index).State!.EnabledNodeIds.IsEmpty);
            var edit=settings.SetVisible("asset",index,0,false);check(edit.Succeeded);
            settings=edit.Settings!;check(JsonNode.Parse(settings.Json)!["overrides"]![0]!["visible"]!.GetValue<bool>()==false);
            using var next=pool.Acquire(FlipCases.Store(root,new(null,"inherited",true,PsdId:20),new(null,"renamed",true,PsdId:10)));
            var recovered=PsdAppearanceSettings.FromJson(settings.Json).Resolve("asset",PsdLayerReferenceIndex.Read(next));
            check(recovered.Succeeded && recovered.State!.EnabledNodeIds.SequenceEqual([0]));
            var inherited=settings.Inherit("asset",index,0);check(inherited.Succeeded);
            check(inherited.Settings!.Resolve("asset",PsdLayerReferenceIndex.Read(next)).State!.EnabledNodeIds.SequenceEqual([0,1]));
            var none=settings.WithFlip("asset",index,PsdFlipState.None);var absent=settings.WithFlip("asset",index,null);
            check(none.Succeeded && absent.Succeeded && none.Settings!.Json!=absent.Settings!.Json);
            check(JsonNode.Parse(none.Settings!.Json)!["flip"]!.GetValue<int>()==0 && JsonNode.Parse(absent.Settings!.Json)!["flip"] is null);
            check(pool.Snapshot().BlockReadCount==4);
        });
        test("C-sparse-radio-B-priority-partial-direction-mask-and-immutable-JSON-disk-roundtrip",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(FlipCases.Store(root,new(null,"*base",true,PsdId:1),new(null,"*A:flipx",false,PsdId:2),
                new(null,"*C:flipy",false,PsdId:3),new(null,"*D:flipxy",false,PsdId:4),new(null,"*B:flipx:flipy",false,PsdId:5)));
            var index=PsdLayerReferenceIndex.Read(doc);var settings=PsdAppearanceSettings.Create("asset");
            foreach(var id in new[]{1,2,3}) { var edit=settings.SetVisible("asset",index,id,true);check(edit.Succeeded);settings=edit.Settings!; }
            var savedA=settings;var b=settings.WithFlip("asset",index,PsdFlipState.Y).Settings!.SetVisible("asset",index,4,true);
            check(b.Succeeded);settings=b.Settings!;
            check(settings.Resolve("asset",index).State!.WithFlip(PsdFlipState.X).EnabledNodeIds.SequenceEqual([4]));
            var reverse=settings.WithFlip("asset",index,PsdFlipState.X).Settings!.SetVisible("asset",index,1,true);
            check(reverse.Succeeded);settings=reverse.Settings!;
            var file=Path.Combine(root,Guid.NewGuid()+".json");File.WriteAllText(file,settings.Json);
            var reloaded=PsdAppearanceSettings.FromJson(File.ReadAllText(file));var state=reloaded.Resolve("asset",index).State!;
            check(state.FlipState==PsdFlipState.X && state.RadioSelectionScopes[4]==PsdDirectionScope.Y);
            foreach(var row in new[]{(PsdFlipState.None,0),(PsdFlipState.X,1),(PsdFlipState.Y,4),(PsdFlipState.XY,3)})
                check(state.WithFlip(row.Item1).EnabledNodeIds.SequenceEqual([row.Item2]));
            var clone=reloaded with { };var before=clone.Json;
            check(clone.SetVisible("asset",index,4,true).State!.WithFlip(PsdFlipState.X).EnabledNodeIds.SequenceEqual([4]));
            check(clone.Json==before && savedA.Resolve("asset",index).State!.WithFlip(PsdFlipState.Y).EnabledNodeIds.SequenceEqual([2]));
            check(pool.Snapshot().BlockReadCount==5);
        });
        test("C-sparse-P2-baseline-alias-and-common-partial-edits-roundtrip-under-hidden-parent",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(FlipCases.Store(root,new(null,"body",false,Group:true,PsdId:1),new(0,"*part",false,PsdId:2),
                new(0,"*part:flipx",true,PsdId:3),new(0,"*Y-only:flipy",false,PsdId:4),
                new(null,"body:flipx",true,Group:true,PsdId:5),new(4,"*part:flipx",true,PsdId:6)));
            var index=PsdLayerReferenceIndex.Read(doc);var settings=PsdAppearanceSettings.Create("asset");
            var original=settings.Resolve("asset",index).State!;check(original.EnabledNodeIds.SequenceEqual([0,2,5]));
            var changed=settings.WithFlip("asset",index,PsdFlipState.Y).Settings!.SetVisible("asset",index,3,true);
            check(changed.Succeeded);settings=changed.Settings!;
            var hidden=settings.SetVisible("asset",index,0,false);check(hidden.Succeeded);
            var restored=PsdAppearanceSettings.FromJson(hidden.Settings!.Json).Resolve("asset",index);check(restored.Succeeded);
            check(!restored.State!.IsLocallyVisible(1) && restored.State.IsLocallyVisible(2));
            check(restored.State.RadioSelectionScopes[1]==(PsdDirectionScope.All&~PsdDirectionScope.Y));
            foreach(var flip in Enum.GetValues<PsdFlipState>()) check(restored.State.WithFlip(flip).PhysicalRadioRepairReason() is null);
            var selected=settings.SetVisible("asset",index,1,true);check(selected.Succeeded);
            check(selected.State!.WithFlip(PsdFlipState.None).EnabledNodeIds.SequenceEqual([0,1]));
            check(original.EnabledNodeIds.SequenceEqual([0,2,5]) && pool.Snapshot().BlockReadCount==6);
        });
        test("C-sparse-name-hierarchy-only-missing-duplicate-ID-and-domain-change-preserve-whole-settings",()=>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var old=pool.Acquire(FlipCases.Store(root,new(null,"plain",false),new(null,"*A:flipx",true,PsdId:10)));
            var index=PsdLayerReferenceIndex.Read(old);var settings=PsdAppearanceSettings.Create("asset").SetVisible("asset",index,0,true).Settings!;
            var before=settings.Json;
            using(var target=pool.Acquire(FlipCases.Store(root,new(null,"plain",false),new(null,"*A:flipx",true,PsdId:10))))
            {
                var result=settings.Resolve("asset",PsdLayerReferenceIndex.Read(target));
                check(!result.Succeeded && ReferenceEquals(result.Settings,settings) && result.Repairs[0].Candidates.SequenceEqual([0]));
                check(!settings.WithFlip("asset",PsdLayerReferenceIndex.Read(target),PsdFlipState.Y).Succeeded && settings.Json==before);
            }
            var owned=PsdAppearanceSettings.Create("asset").SetVisible("asset",index,1,true).Settings!;
            Spec[][] variants=[
                [new(null,"missing",false,PsdId:20)],
                [new(null,"*A:flipx",true,PsdId:10),new(null,"ordinary",false,PsdId:10)],
                [new(null,"*A:flipy",true,PsdId:10)]
            ];
            foreach(var fixture in variants)
            {
                using var target=pool.Acquire(FlipCases.Store(root,fixture));var result=owned.Resolve("asset",PsdLayerReferenceIndex.Read(target));
                check(!result.Succeeded && result.State is null && ReferenceEquals(result.Settings,owned));
            }
        });
        test("C-sparse-unknown-data-malformed-schema-and-all-or-nothing-recovery",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(FlipCases.Store(root,new Spec(null,"plain",true,PsdId:uint.MaxValue)));
            var index=PsdLayerReferenceIndex.Read(doc);check(index.Capture(0).LayerId==uint.MaxValue);
            var settings=PsdAppearanceSettings.Create("asset").SetVisible("asset",index,0,false).Settings!;
            var json=JsonNode.Parse(settings.Json)!;json["future"] = new JsonObject { ["opaque"] = new JsonArray(1,2,3) };
            json["overrides"]![0]!["target"]!["futureRef"]="keep";json["overrides"]![0]!["futureOp"]=17;
            var extended=PsdAppearanceSettings.FromJson(json.ToJsonString());var edited=extended.WithFlip("asset",index,PsdFlipState.X);
            check(edited.Succeeded);var kept=JsonNode.Parse(edited.Settings!.Json)!;
            check(JsonNode.DeepEquals(kept["future"],json["future"]) && kept["overrides"]![0]!["target"]!["futureRef"]!.GetValue<string>()=="keep");
            check(kept["overrides"]![0]!["futureOp"]!.GetValue<int>()==17 && extended.Json==json.ToJsonString());
            var known=JsonNode.Parse(extended.Json)!;known["version"]=999;var unknown=PsdAppearanceSettings.FromJson(known.ToJsonString());
            check(!unknown.Resolve("asset",index).Succeeded && ReferenceEquals(unknown.WithFlip("asset",index,PsdFlipState.Y).Settings,unknown));
            check((unknown with { }).Json==unknown.Json);
            var bad=JsonNode.Parse(extended.Json)!;bad["overrides"]!.AsArray().Add(bad["overrides"]![0]!.DeepClone());
            var duplicate=PsdAppearanceSettings.FromJson(bad.ToJsonString());check(!duplicate.Resolve("asset",index).Succeeded);
            bad=JsonNode.Parse(extended.Json)!;bad["overrides"]![0]!["proof"]![0]!["Scope"]=0;
            check(!PsdAppearanceSettings.FromJson(bad.ToJsonString()).Resolve("asset",index).Succeeded);
            check(!PsdAppearanceSettings.FromJson("{\"version\":1,\"defaults\":1,\"asset\":\"asset\",\"overrides\":[null]}").Resolve("asset",index).Succeeded);
            check(!extended.Resolve("foreign-asset",index).Succeeded);
            var rejected=false;try{PsdAppearanceSettings.FromJson("{\"version\":1,\"version\":999}");}catch(System.Text.Json.JsonException){rejected=true;}check(rejected);
            check(pool.Snapshot().BlockReadCount==1);
        });
        test("C-sparse-mixed-resolved-missing-operations-atomic-and-direction-only-first-Y-unchanged",()=>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var old=pool.Acquire(FlipCases.Store(root,new(null,"ordinary",false,PsdId:1),new(null,"removed",false,PsdId:2)));
            var index=PsdLayerReferenceIndex.Read(old);
            var saved=PsdAppearanceSettings.Create("asset").SetVisible("asset",index,0,true).Settings!.SetVisible("asset",index,1,true).Settings!;
            using var next=pool.Acquire(FlipCases.Store(root,new Spec(null,"ordinary",false,PsdId:1)));
            var failed=saved.Resolve("asset",PsdLayerReferenceIndex.Read(next));
            check(!failed.Succeeded && failed.State is null && ReferenceEquals(failed.Settings,saved));
            check(failed.Repairs.Length==1 && PsdVisibilityState.Create(next.Notation).EnabledNodeIds.IsEmpty);
            next.Dispose();
            using var direction=pool.Acquire(FlipCases.Store(root,new(null,"*A:flipx",true,PsdId:11),new(null,"*B:flipy",false,PsdId:12),new(null,"!force",false,PsdId:13)));
            var di=PsdLayerReferenceIndex.Read(direction);var unowned=PsdAppearanceSettings.Create("asset");
            var y=unowned.WithFlip("asset",di,PsdFlipState.Y);check(y.Succeeded && y.State!.EnabledNodeIds.SequenceEqual([2]));
            check(PsdAppearanceSettings.FromJson(y.Settings!.Json).Resolve("asset",di).State!.EnabledNodeIds.SequenceEqual([2]));
            var rejected=unowned.SetVisible("asset",di,2,false);
            check(!rejected.Succeeded && ReferenceEquals(rejected.Settings,unowned));
            check(!unowned.SetVisible("asset",di,0,false).Succeeded);
        });
    }
}
