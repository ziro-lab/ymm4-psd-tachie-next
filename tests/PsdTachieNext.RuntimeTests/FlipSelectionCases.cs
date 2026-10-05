using PsdTachieNext.Core;
using Spec = FlipCases.Spec;

internal static class FlipSelectionCases
{
    internal static void Run(Action<string,Action> test,Action<bool> check,string root)
    {
        test("C-flip-edit-normal-choice-in-each-orientation-retains-latest-choice-on-return",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,
                new(null,"body",true,Group:true),new(0,"*same",true),new(0,"*same",false),new(0,"overlay",true),
                new(null,"body:flipx",false,Group:true),new(4,"*same",false),new(4,"*same",true),new(4,"overlay",true),
                new(null,"body:flipy",false,Group:true),new(8,"*same",true),new(8,"*same",false),new(8,"overlay",true),
                new(null,"body:flipxy",false,Group:true),new(12,"*same",true),new(12,"*same",false),new(12,"overlay",true)));
            var original=PsdVisibilityState.CreateVerified(doc.Notation);
            foreach(var flip in new[]{PsdFlipState.None,PsdFlipState.X,PsdFlipState.Y,PsdFlipState.XY})
            {
                var before=original.WithFlip(flip);var edited=before.SetVisible(2,true).SetVisible(3,false);
                var rootId=flip switch{PsdFlipState.X=>4,PsdFlipState.Y=>8,PsdFlipState.XY=>12,_=>0};
                check(Active(doc,edited).SequenceEqual([rootId,rootId+2]));
                check(edited.FlipState==flip && edited.IsLocallyVisible(2) && !edited.IsLocallyVisible(1));
                check(!edited.IsLocallyVisible(3) && before.IsLocallyVisible(1) && before.IsLocallyVisible(3));
                check(ReferenceEquals(edited,edited.WithFlip(flip)) && ReferenceEquals(edited,edited.SetVisible(2,true)));
                var returned=edited.WithFlip(PsdFlipState.None);
                check(Active(doc,returned).SequenceEqual([0,2]));
                foreach(var next in new[]{PsdFlipState.X,PsdFlipState.Y,PsdFlipState.XY,PsdFlipState.None})
                    check(edited.WithFlip(next).WithFlip(PsdFlipState.None).EnabledNodeIds.SequenceEqual(returned.EnabledNodeIds));
                check(original.IsLocallyVisible(1) && !original.IsLocallyVisible(2));
            }
            check(doc.Manifest.Nodes[1].DefaultVisible && !doc.Manifest.Nodes[2].DefaultVisible);
            Reject(()=>original.WithFlip(PsdFlipState.X).SetVisible(6,true),check);
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-flip-hidden-parent-edit-shared-X-Y-and-XY-return-keeps-new-choice",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,new(null,"parent",false,Group:true),
                new(0,"body",true,Group:true),new(1,"*same",true),new(1,"*same",false),
                new(0,"body:flipx:flipy",false,Group:true),new(4,"*same",true),new(4,"*same",false),
                new(0,"body:flipxy",false,Group:true),new(7,"*same",true),new(7,"*same",false)));
            var original=PsdVisibilityState.CreateVerified(doc.Notation);
            var changed=original.WithFlip(PsdFlipState.X).SetVisible(3,true);
            foreach(var flip in new[]{PsdFlipState.Y,PsdFlipState.XY,PsdFlipState.X,PsdFlipState.None})
            {
                changed=changed.WithFlip(flip);
                check(Active(doc,changed).IsEmpty);
                check(changed.IsLocallyVisible(3) && !changed.IsLocallyVisible(2));
                var expected=flip switch{PsdFlipState.X or PsdFlipState.Y=>new[]{0,4,6},PsdFlipState.XY=>new[]{0,7,9},_=>new[]{0,1,3}};
                check(Active(doc,changed.SetVisible(0,true)).SequenceEqual(expected));
            }
            check(Active(doc,changed.SetVisible(0,true)).SequenceEqual([0,1,3]));
            check(!original.IsLocallyVisible(3) && pool.Snapshot().BlockReadCount==0);
        });
        test("C-flip-immutable-state-snapshots-undo-redo-and-generation-rejection",()=>
        {
            using var pool=new SharedDocumentPool(128,2);
            Spec[] specs=[new(null,"body",true,Group:true),new(0,"*a",true),new(0,"*b",false),
                new(null,"body:flipx",false,Group:true),new(3,"*a",true),new(3,"*b",false)];
            using var doc=pool.Acquire(Store(root,specs));var start=PsdVisibilityState.CreateVerified(doc.Notation);
            var flipped=start.WithFlip(PsdFlipState.X);var chosen=flipped.SetVisible(2,true);var returned=chosen.WithFlip(PsdFlipState.None);
            PsdVisibilityState[] snapshots=[start,flipped,chosen,returned];int[][] expected=[[0,1],[3,4],[3,5],[0,2]];
            foreach(var i in new[]{3,2,1,0,1,2,3})
            {
                var restored=snapshots[i];check(Active(doc,restored).SequenceEqual(expected[i]));
                check(ReferenceEquals(restored,restored.WithFlip(restored.FlipState)));
            }
            check(snapshots[0].IsLocallyVisible(1) && snapshots[2].IsLocallyVisible(2));
            using var changedGeneration=pool.Acquire(Store(root,specs));
            Reject(()=>RenderPlan.CreateNotationVisibility(changedGeneration.Manifest,returned),check);
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-flip-direction-only-edits-and-common-alias-cleanup-no-guessed-correspondence",()=>
        {
            using var pool=new SharedDocumentPool(128,3);
            using var ordinary=pool.Acquire(Store(root,new(null,"body",true,Group:true),new(0,"left-only",true),
                new(null,"body:flipx",false,Group:true),new(2,"right-only",false)));
            var changed=PsdVisibilityState.Create(ordinary.Notation).WithFlip(PsdFlipState.X).SetVisible(3,true);
            check(changed.IsLocallyVisible(3) && Active(ordinary,changed).SequenceEqual([2,3]));
            check(Active(ordinary,changed.WithFlip(PsdFlipState.None)).SequenceEqual([0,1]));
            check(changed.WithFlip(PsdFlipState.None).WithFlip(PsdFlipState.X).EnabledNodeIds.SequenceEqual(changed.EnabledNodeIds));
            check(changed.Bindings.Diagnostics.Any(d=>d.Issue==PsdFlipBindingIssue.MissingChild && d.NodeId==3));
            using var radio=pool.Acquire(Store(root,new(null,"body",true,Group:true),new(0,"*normal",true),
                new(null,"body:flipx",false,Group:true),new(2,"*normal",false),new(2,"*right-only",false)));
            var attempted=PsdVisibilityState.Create(radio.Notation).WithFlip(PsdFlipState.X).SetVisible(4,true);
            check(attempted.IsLocallyVisible(4) && attempted.EnabledNodeIds.Contains(4));
            check(Active(radio,attempted).SequenceEqual([2,4]));
            check(attempted.Bindings.Diagnostics.Any(d=>d.Issue==PsdFlipBindingIssue.MissingChild && d.NodeId==4));
            using var nested=pool.Acquire(Store(root,new(null,"body",false,Group:true),new(0,"part",false),new(0,"part:flipx",true),
                new(null,"body:flipx",true,Group:true),new(3,"part:flipx",true)));
            var mixed=PsdVisibilityState.Create(nested.Notation).WithFlip(PsdFlipState.X).SetVisible(1,false);
            check(!mixed.IsLocallyVisible(1) && !mixed.IsLocallyVisible(2));
            check(Active(nested,mixed).SequenceEqual([3]));
            check(Active(nested,mixed.WithFlip(PsdFlipState.None)).SequenceEqual([0]));
            Reject(()=>mixed.SetVisible(2,false),check);
            check(pool.Snapshot().BlockReadCount==0);
        });
    }
    private static System.Collections.Immutable.ImmutableArray<int> Active(SharedDocumentLease doc,PsdVisibilityState state)
        =>RenderPlan.CreateNotationVisibility(doc.Manifest,state).ActiveNodeIds;
    private static string Store(string root,params Spec[] specs)=>FlipCases.Store(root,specs);
    private static void Reject(Action action,Action<bool> check)
    {var rejected=false;try{action();}catch(ArgumentException){rejected=true;}check(rejected);}
}
