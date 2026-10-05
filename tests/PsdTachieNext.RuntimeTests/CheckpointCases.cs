using System.Collections.Immutable;
using System.Text.Json;
using PsdTachieNext.Core;
using Spec = FlipCases.Spec;

internal static class CheckpointCases
{
    internal static void Run(Action<string, Action> test, Action<bool> check, string root)
    {
        test("C-checkpoint-rejects-same-origin-P2-double-radio-even-under-hidden-parent", () =>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(FlipCases.Store(root,new(null,"body",false,Group:true),
                new(0,"*part",false),new(0,"*part:flipx",true),
                new(null,"body:flipx",true,Group:true),new(3,"*part:flipx",true)));
            var original=PsdVisibilityState.Create(doc.Notation);
            check(original.EnabledNodeIds.SequenceEqual([0,2,4]));
            check(Active(doc,original).SequenceEqual([0,2]));
            var outcomes=new List<bool>();
            foreach(var hidden in new[]{false,true})
            {
                var state=hidden?original.SetVisible(0,false):original;
                var checkpoint=PsdVisibilityCheckpoint.Capture("asset",state);
                check(checkpoint.Restore("asset",doc.Notation).Succeeded);
                var corrupted=checkpoint with { Nodes=checkpoint.Nodes.SetItem(1,checkpoint.Nodes[1] with { LocallyVisible=true }) };
                var json=JsonSerializer.Serialize(corrupted);
                foreach(var flip in Enum.GetValues<PsdFlipState>())
                {
                    var input=corrupted with { Flip=flip };
                    var before=JsonSerializer.Serialize(input);
                    var recovery=input.Restore("asset",doc.Notation);
                    if(recovery.State is { } accepted)
                        Console.WriteLine($"CHECKPOINT_P2_REPRO hidden={hidden} savedFlip={flip} NoneRaw=[{string.Join(",",accepted.WithFlip(PsdFlipState.None).EnabledNodeIds)}] NoneActive=[{string.Join(",",Active(doc,accepted.WithFlip(PsdFlipState.None)))}]");
                    outcomes.Add(!recovery.Succeeded && recovery.State is null);
                    check(ReferenceEquals(recovery.Checkpoint,input) && JsonSerializer.Serialize(input)==before);
                }
                check(JsonSerializer.Serialize(corrupted)==json);
            }
            check(outcomes.All(rejected=>rejected));
            check(original.EnabledNodeIds.SequenceEqual([0,2,4]) && pool.Snapshot().BlockReadCount==0);
        });
        test("C-checkpoint-unsupported-target-notation-and-binding-return-preserved-repair", () =>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var old=pool.Acquire(FlipCases.Store(root,new Spec(null,"plain",true)));
            var checkpoint=PsdVisibilityCheckpoint.Capture("asset",PsdVisibilityState.Create(old.Notation));
            var before=JsonSerializer.Serialize(checkpoint);
            Spec[][] targets=[
                [new(null,"plain",true),new(null,"flipx",false)],
                [new(null,"plain",true),new(null,"body",true,Group:true),new(null,"body",false,Group:true),
                    new(null,"body:flipx",false,Group:true)]
            ];
            foreach(var fixture in targets)
            {
                using var target=pool.Acquire(FlipCases.Store(root,fixture));
                var result=checkpoint.Restore("asset",target.Notation,allowUniqueHierarchyRecovery:true);
                check(!result.Succeeded && result.State is null && result.RepairReason is not null);
                check(ReferenceEquals(result.Checkpoint,checkpoint) && JsonSerializer.Serialize(checkpoint)==before);
            }
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-checkpoint-pure-save-reload-preserves-partial-radio-scopes-and-flip", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var doc = pool.Acquire(FlipCases.Store(root, new(null,"*base",true), new(null,"*A:flipx",false),
                new(null,"*C:flipy",false), new(null,"*D:flipxy",false), new(null,"*B:flipx:flipy",false)));
            var a = PsdVisibilityState.Create(doc.Notation).WithFlip(PsdFlipState.X).SetVisible(1,true)
                .WithFlip(PsdFlipState.Y).SetVisible(2,true).WithFlip(PsdFlipState.XY).SetVisible(3,true);
            var b = a.WithFlip(PsdFlipState.Y).SetVisible(4,true);
            var reverse = b.WithFlip(PsdFlipState.X).SetVisible(1,true);
            foreach (var state in new[] { a, b, reverse })
            {
                var checkpoint = PsdVisibilityCheckpoint.Capture("asset", state);
                var path = Path.Combine(root, Guid.NewGuid()+".json");
                File.WriteAllText(path, JsonSerializer.Serialize(checkpoint));
                var reloaded = JsonSerializer.Deserialize<PsdVisibilityCheckpoint>(File.ReadAllText(path))!;
                var recovery = reloaded.Restore("asset", doc.Notation);
                check(recovery.Succeeded && ReferenceEquals(recovery.Checkpoint,reloaded));
                check(recovery.State!.FlipState == state.FlipState);
                foreach (var flip in Enum.GetValues<PsdFlipState>())
                {
                    check(recovery.State.WithFlip(flip).EnabledNodeIds.SequenceEqual(state.WithFlip(flip).EnabledNodeIds));
                    check(Active(doc,recovery.State.WithFlip(flip)).SequenceEqual(Active(doc,state.WithFlip(flip))));
                }
                for (var id = 0; id < doc.Manifest.Nodes.Length; id++)
                    check(recovery.State.IsLocallyVisible(id) == state.IsLocallyVisible(id));
                check(recovery.State.RadioSelectionScopes.OrderBy(p=>p.Key).SequenceEqual(state.RadioSelectionScopes.OrderBy(p=>p.Key)));
                var clone = reloaded with { };
                var edited = clone.Restore("asset",doc.Notation).State!.WithFlip(PsdFlipState.Y).SetVisible(4,true);
                check(Active(doc,edited.WithFlip(PsdFlipState.X)).SequenceEqual([4]));
                check(JsonSerializer.Serialize(clone)==JsonSerializer.Serialize(reloaded));
            }
            check(Active(doc,reverse.WithFlip(PsdFlipState.X)).SequenceEqual([1]));
            check(Active(doc,reverse.WithFlip(PsdFlipState.Y)).SequenceEqual([4]));
            check(reverse.RadioSelectionScopes[4]==PsdDirectionScope.Y);
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-checkpoint-protected-P2-alias-and-one-pass-None-restore-exactly", () =>
        {
            Spec[][] fixtures = [
                [new(null,"body",false,Group:true),new(0,"*part",false),new(0,"*part:flipx",true),
                    new(null,"body:flipx",true,Group:true),new(3,"*part:flipx",true)],
                [new(null,"body",true,Group:true),new(0,"part",true),new(0,"part:flipx",false),
                    new(null,"body:flipx",false,Group:true),new(3,"part:flipx",true)]
            ];
            foreach (var fixture in fixtures)
            {
                using var pool=new SharedDocumentPool(128,1); using var doc=pool.Acquire(FlipCases.Store(root,fixture));
                var original=PsdVisibilityState.Create(doc.Notation);
                var copy=JsonSerializer.Deserialize<PsdVisibilityCheckpoint>(JsonSerializer.Serialize(PsdVisibilityCheckpoint.Capture("asset",original)))!;
                var restored=copy.Restore("asset",doc.Notation);
                check(restored.Succeeded);
                check(restored.State!.EnabledNodeIds.SequenceEqual(original.EnabledNodeIds));
                foreach(var flip in Enum.GetValues<PsdFlipState>())
                    check(Active(doc,restored.State.WithFlip(flip)).SequenceEqual(Active(doc,original.WithFlip(flip))));
                check(pool.Snapshot().BlockReadCount==0);
            }
        });
        test("C-checkpoint-reference-recovery-reordered-new-generation-and-literal-hierarchy", () =>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var old=pool.Acquire(FlipCases.Store(root,new(null,"a/b\\%日本",true,Group:true),
                new(0,"*one",true),new(0,"*two",false),new(null,"plain",true)));
            var chosen=PsdVisibilityState.Create(old.Notation).SetVisible(2,true).SetVisible(0,false).WithFlip(PsdFlipState.Y);
            var checkpoint=PsdVisibilityCheckpoint.Capture("asset",chosen);
            using var next=pool.Acquire(FlipCases.Store(root,new(null,"plain",true),new(null,"added",false),
                new(null,"a/b\\%日本",true,Group:true),new(2,"*two",false),new(2,"*one",true)));
            check(next.Manifest.GenerationId!=old.Manifest.GenerationId);
            check(!checkpoint.Restore("asset",next.Notation).Succeeded);
            var recovered=checkpoint.Restore("asset",next.Notation,allowUniqueHierarchyRecovery:true);
            check(recovered.Succeeded && recovered.State!.FlipState==PsdFlipState.Y);
            check(recovered.State!.IsLocallyVisible(3) && !recovered.State.IsLocallyVisible(4));
            check(!recovered.State.IsLocallyVisible(2) && !recovered.State.IsLocallyVisible(1));
            check(Active(next,recovered.State).SequenceEqual([0]));
            check(Active(next,recovered.State.SetVisible(2,true)).SequenceEqual([0,2,3]));
            check(Active(old,chosen).SequenceEqual([3]));
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-checkpoint-missing-ambiguous-moved-and-scope-change-are-atomic-repair", () =>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var old=pool.Acquire(FlipCases.Store(root,new(null,"group",true,Group:true),new(0,"part",false)));
            var checkpoint=PsdVisibilityCheckpoint.Capture("asset",PsdVisibilityState.Create(old.Notation).SetVisible(1,true));
            var before=JsonSerializer.Serialize(checkpoint);
            Spec[][] bad = [
                [new(null,"group",true,Group:true)],
                [new(null,"group",true,Group:true),new(0,"part",false),new(0,"part",true)],
                [new(null,"group",true,Group:true),new(null,"part",false)],
                [new(null,"group",true,Group:true),new(0,"renamed",false)],
                [new(null,"group",true,Group:true),new(0,"part",false),new(null,"group:flipx",false,Group:true),new(2,"part",false)]
            ];
            foreach(var fixture in bad)
            {
                using var target=pool.Acquire(FlipCases.Store(root,fixture));
                var recovery=checkpoint.Restore("asset",target.Notation,allowUniqueHierarchyRecovery:true);
                check(!recovery.Succeeded && recovery.State is null && recovery.RepairReason is not null);
                check(ReferenceEquals(recovery.Checkpoint,checkpoint) && JsonSerializer.Serialize(checkpoint)==before);
            }
            check(!checkpoint.Restore("other-asset",old.Notation).Succeeded);
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-checkpoint-duplicates-same-generation-only-and-no-ordinal-guess", () =>
        {
            using var pool=new SharedDocumentPool(128,2);
            Spec[] fixture=[new(null,"group",true,Group:true),new(0,"same",true),new(0,"same",false)];
            using var old=pool.Acquire(FlipCases.Store(root,fixture));
            using var next=pool.Acquire(FlipCases.Store(root,fixture));
            var checkpoint=PsdVisibilityCheckpoint.Capture("asset",PsdVisibilityState.Create(old.Notation).SetVisible(2,true).SetVisible(1,false));
            check(checkpoint.Restore("asset",old.Notation).Succeeded);
            check(!checkpoint.Restore("asset",next.Notation,allowUniqueHierarchyRecovery:true).Succeeded);
            var corrupted=checkpoint with { Nodes=checkpoint.Nodes.SetItem(2,checkpoint.Nodes[2] with { DuplicateIndex=0 }) };
            check(!corrupted.Restore("asset",old.Notation).Succeeded);
        });
        test("C-checkpoint-deep-reference-forest-and-wide-radio-roundtrip-without-pixels", () =>
        {
            using var pool=new SharedDocumentPool(4096,1);
            var deep=Enumerable.Range(0,4096).Select(id=>new Spec(id==0?null:id-1,"group",true,Group:true)).ToArray();
            using(var doc=pool.Acquire(FlipCases.Store(root,deep)))
            {
                var state=PsdVisibilityState.Create(doc.Notation).SetVisible(2048,false);
                var checkpoint=PsdVisibilityCheckpoint.Capture("asset",state);
                var json=JsonSerializer.Serialize(checkpoint);
                var restored=JsonSerializer.Deserialize<PsdVisibilityCheckpoint>(json)!.Restore("asset",doc.Notation);
                check(restored.Succeeded && restored.State!.EnabledNodeIds.SequenceEqual(state.EnabledNodeIds));
                check(!restored.State!.IsLocallyVisible(2048) && restored.State.IsLocallyVisible(4095));
            }
            using(var doc=pool.Acquire(FlipCases.Store(root,Enumerable.Range(0,512).Select(id=>
                new Spec(null,"*d"+id+(id%2==0?":flipx":":flipy"),id==0)).ToArray())))
            {
                var state=PsdVisibilityState.Create(doc.Notation).WithFlip(PsdFlipState.Y).SetVisible(257,true);
                var checkpoint=PsdVisibilityCheckpoint.Capture("asset",state);
                var restored=JsonSerializer.Deserialize<PsdVisibilityCheckpoint>(JsonSerializer.Serialize(checkpoint))!.Restore("asset",doc.Notation);
                check(restored.Succeeded);
                check(Active(doc,restored.State!).SequenceEqual([257]));
                check(Active(doc,restored.State!.WithFlip(PsdFlipState.X)).SequenceEqual([0]));
                check(Active(doc,restored.State!.WithFlip(PsdFlipState.None)).IsEmpty);
            }
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-checkpoint-new-radio-default-conflict-preserves-saved-intent", () =>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var old=pool.Acquire(FlipCases.Store(root,new(null,"*one",false),new(null,"*two",true)));
            var checkpoint=PsdVisibilityCheckpoint.Capture("asset",PsdVisibilityState.Create(old.Notation));
            using var target=pool.Acquire(FlipCases.Store(root,new(null,"*added",true),new(null,"*one",false),new(null,"*two",true)));
            var result=checkpoint.Restore("asset",target.Notation,allowUniqueHierarchyRecovery:true);
            check(!result.Succeeded && result.State is null && ReferenceEquals(result.Checkpoint,checkpoint));
            check(result.RepairReason=="Radio choices conflict after reference recovery.");
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-checkpoint-invalid-version-mask-origin-and-reference-never-default", () =>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(FlipCases.Store(root,new(null,"*A:flipx",true),new(null,"*B:flipy",false),new(null,"!force",false)));
            var checkpoint=PsdVisibilityCheckpoint.Capture("asset",PsdVisibilityState.Create(doc.Notation));
            foreach(var bad in new[] {
                checkpoint with { Flip=(PsdFlipState)100 },
                checkpoint with { Nodes=default },
                checkpoint with { Nodes=checkpoint.Nodes.RemoveAt(2) },
                checkpoint with { Nodes=checkpoint.Nodes.SetItem(0,checkpoint.Nodes[0] with { RadioScopes=PsdDirectionScope.All }) },
                checkpoint with { Nodes=checkpoint.Nodes.SetItem(0,checkpoint.Nodes[0] with { RadioScopes=0 }) },
                checkpoint with { Nodes=checkpoint.Nodes.SetItem(0,checkpoint.Nodes[0] with { OriginKey=1 }) },
                checkpoint with { Nodes=checkpoint.Nodes.SetItem(2,checkpoint.Nodes[2] with { LocallyVisible=false }) },
                checkpoint with { Nodes=checkpoint.Nodes.SetItem(0,checkpoint.Nodes[0] with { ParentKey=0 }) }
            }) check(!bad.Restore("asset",doc.Notation).Succeeded);
            var rejected=false; try { (checkpoint with { Version=999 }).Restore("asset",doc.Notation); } catch(NotSupportedException) { rejected=true; }
            check(rejected);
            check(checkpoint.Restore("asset",doc.Notation).State!.WithFlip(PsdFlipState.Y).EnabledNodeIds.SequenceEqual([2]));
            // An absent checkpoint is the old parameter behavior: use the existing default factory.
            var legacy=PsdVisibilityState.Create(doc.Notation);
            check(legacy.WithFlip(PsdFlipState.Y).EnabledNodeIds.SequenceEqual([2]));
            check(pool.Snapshot().BlockReadCount==0);
        });
    }

    private static ImmutableArray<int> Active(SharedDocumentLease doc,PsdVisibilityState state)
        => RenderPlan.CreateNotationVisibility(doc.Manifest,state).ActiveNodeIds;
}
