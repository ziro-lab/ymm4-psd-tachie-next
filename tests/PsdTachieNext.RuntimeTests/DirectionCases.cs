using PsdTachieNext.Core;
using Spec = FlipCases.Spec;

internal static class DirectionCases
{
    internal static void Run(Action<string,Action> test,Action<bool> check,string root)
    {
        test("C-direction-initial-radio-common-alias-prevents-duplicate-fallback-and-roundtrip",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,new(null,"body",false,Group:true),
                new(0,"*part",false),new(0,"*part:flipx",true),
                new(null,"body:flipx",true,Group:true),new(3,"*part:flipx",true)));
            var initial=PsdVisibilityState.Create(doc.Notation);
            Console.WriteLine("RADIO_P2_INITIAL checked=["+string.Join(",",initial.EnabledNodeIds)+"] active=["+
                string.Join(",",Active(doc,initial))+"]; expected checked=[0,2,4] active=[0,2]");
            check(initial.EnabledNodeIds.SequenceEqual([0,2,4]));
            check(Active(doc,initial).SequenceEqual([0,2]));
            check(!initial.IsLocallyVisible(1) && initial.IsLocallyVisible(2));
            check(initial.Bindings.Selection.OriginNodeIds[2]==1 && initial.Bindings.Selection.OriginNodeIds[4]==1);
            var current=initial;
            foreach(var row in new[]{(PsdFlipState.None,new[]{0,2}),(PsdFlipState.X,new[]{3,4}),
                (PsdFlipState.None,new[]{0,2}),(PsdFlipState.Y,new[]{0,1}),
                (PsdFlipState.None,new[]{0,2}),(PsdFlipState.XY,new[]{0,1}),(PsdFlipState.None,new[]{0,2})})
            {
                current=current.WithFlip(row.Item1);
                check(Active(doc,current).SequenceEqual(row.Item2));
                check(current.WithFlip(PsdFlipState.None).EnabledNodeIds.SequenceEqual([0,2,4]));
                check(!current.IsLocallyVisible(1) && current.IsLocallyVisible(2));
                check(ReferenceEquals(current,current.WithFlip(row.Item1)));
            }
            var edited=initial.WithFlip(PsdFlipState.X).SetVisible(1,true).WithFlip(PsdFlipState.None);
            check(Active(doc,edited).SequenceEqual([0,1]));
            check(Active(doc,initial).SequenceEqual([0,2]) && initial.EnabledNodeIds.SequenceEqual([0,2,4]));
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-direction-radio-only-initial-unselected-Y-stays-empty-pending-policy",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,new(null,"*A:flipx",true),new(null,"*B:flipy",false)));
            var initial=PsdVisibilityState.Create(doc.Notation);
            check(initial.IsLocallyVisible(0) && !initial.IsLocallyVisible(1));
            check(Active(doc,initial).IsEmpty);
            foreach(var row in new[]{(PsdFlipState.Y,Array.Empty<int>()),(PsdFlipState.X,new[]{0}),
                (PsdFlipState.XY,Array.Empty<int>()),(PsdFlipState.Y,Array.Empty<int>())})
            {
                var current=initial.WithFlip(row.Item1);
                check(Active(doc,current).SequenceEqual(row.Item2));
                check(current.IsLocallyVisible(0) && !current.IsLocallyVisible(1));
            }
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-direction-missing-root-scopes-force-radio-and-shared-X-Y-memory",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,new(null,"ordinary",true),new(null,"cape:flipx",false),
                new(null,"!shine:flipy",false),new(null,"*pose:flipxy",false),new(null,"both:flipx:flipy",false)));
            var initial=PsdVisibilityState.Create(doc.Notation);var state=initial.SetVisible(1,true).SetVisible(4,true);
            check(initial.EnabledNodeIds.SequenceEqual([0]) && !initial.IsLocallyVisible(1));
            check(state.Bindings.Selection.DirectionOnlyScopes[1]==PsdDirectionScope.X);
            check(state.Bindings.Selection.DirectionOnlyScopes[4]==(PsdDirectionScope.X|PsdDirectionScope.Y));
            foreach(var row in new[]{(PsdFlipState.None,new[]{0}),(PsdFlipState.X,new[]{0,1,4}),
                (PsdFlipState.Y,new[]{0,2,4}),(PsdFlipState.XY,new[]{0,3}),(PsdFlipState.None,new[]{0})})
            {
                state=state.WithFlip(row.Item1);check(Active(doc,state).SequenceEqual(row.Item2));
                check(state.IsLocallyVisible(1) && state.IsLocallyVisible(2) && state.IsLocallyVisible(3) && state.IsLocallyVisible(4));
                check(ReferenceEquals(state,state.WithFlip(row.Item1)));
            }
            check(ReferenceEquals(state,state.SetVisible(2,false)) && ReferenceEquals(state,state.SetVisible(3,false)));
            check(ReferenceEquals(state,state.SetVisible(1,true)));
            check(!Active(doc,state.SetVisible(4,false).WithFlip(PsdFlipState.Y)).Contains(4));
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-direction-radio-overrides-transfer-common-edit-updates-applicable-directions",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,
                new(null,"body",true,Group:true),new(0,"*common",true),new(0,"*other",false),new(0,"!shared",false),
                new(null,"body:flipx",false,Group:true),new(4,"*common",false),new(4,"*other",true),
                new(4,"*extra",false),new(4,"*more",false),new(4,"!extra",false),
                new(null,"body:flipy",false,Group:true),new(10,"*common",true),new(10,"*other",false),
                new(10,"*extra",false),new(10,"!extra",false),
                new(null,"body:flipxy",false,Group:true),new(15,"*common",true),new(15,"*other",false),
                new(15,"*extra",false),new(15,"!extra",false)));
            var original=PsdVisibilityState.Create(doc.Notation);
            var first=original.WithFlip(PsdFlipState.X).SetVisible(7,true);
            check(Active(doc,first).SequenceEqual([4,7,9]));
            check(first.IsLocallyVisible(1) && Active(doc,first.WithFlip(PsdFlipState.None)).SequenceEqual([0,1,3]));
            check(ReferenceEquals(first,first.SetVisible(7,false)));
            var remembered=first.SetVisible(8,true).WithFlip(PsdFlipState.Y).SetVisible(13,true)
                .WithFlip(PsdFlipState.XY).SetVisible(18,true);
            check(!remembered.IsLocallyVisible(7) && remembered.IsLocallyVisible(8));
            foreach(var row in new[]{(PsdFlipState.X,new[]{4,8,9}),(PsdFlipState.Y,new[]{10,13,14}),
                (PsdFlipState.XY,new[]{15,18,19}),(PsdFlipState.None,new[]{0,1,3})})
                check(Active(doc,remembered.WithFlip(row.Item1)).SequenceEqual(row.Item2));
            check(ReferenceEquals(remembered,remembered.SetVisible(9,false)));
            var edited=remembered.WithFlip(PsdFlipState.X).SetVisible(2,true);
            check(Active(doc,edited).SequenceEqual([4,6,9]));
            check(Active(doc,edited.WithFlip(PsdFlipState.None)).SequenceEqual([0,2,3]));
            check(!edited.IsLocallyVisible(8) && !edited.IsLocallyVisible(13) && !edited.IsLocallyVisible(18));
            check(Active(doc,edited.WithFlip(PsdFlipState.Y)).SequenceEqual([10,12,14]));
            check(Active(doc,edited.WithFlip(PsdFlipState.XY)).SequenceEqual([15,17,19]));
            var beforeY=edited.WithFlip(PsdFlipState.Y);var clearY=beforeY.SetVisible(2,true);
            check(ReferenceEquals(clearY,beforeY));
            check(Active(doc,clearY).SequenceEqual([10,12,14]) && !clearY.IsLocallyVisible(18));
            check(ReferenceEquals(clearY,clearY.SetVisible(2,true)));
            check(Active(doc,remembered.WithFlip(PsdFlipState.X)).SequenceEqual([4,8,9])); // immutable Undo snapshot
            check(Active(doc,edited.WithFlip(PsdFlipState.X)).SequenceEqual([4,6,9])); // Redo snapshot
            check(original.IsLocallyVisible(1) && !original.IsLocallyVisible(7) && pool.Snapshot().BlockReadCount==0);
        });
        test("C-direction-hidden-parent-nested-group-retains-shared-X-Y-and-separate-XY",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,new(null,"parent",false,Group:true),
                new(0,"body",true,Group:true),new(1,"same",true),new(1,"left-only",true),
                new(0,"body:flipx:flipy",false,Group:true),new(4,"same",false),new(4,"only-group",true,Group:true),
                new(6,"!force",false),new(6,"*one",true),new(6,"*two",false),
                new(0,"body:flipxy",false,Group:true),new(10,"same",true),new(10,"only-group",true,Group:true),
                new(12,"!force",false),new(12,"*one",true),new(12,"*two",false)));
            var state=PsdVisibilityState.Create(doc.Notation).WithFlip(PsdFlipState.X).SetVisible(9,true)
                .WithFlip(PsdFlipState.XY).SetVisible(15,true).SetVisible(6,false);
            foreach(var flip in new[]{PsdFlipState.None,PsdFlipState.X,PsdFlipState.Y,PsdFlipState.XY})
            {
                check(Active(doc,state.WithFlip(flip)).IsEmpty);
                check(state.IsLocallyVisible(9) && state.IsLocallyVisible(15));
            }
            check(Active(doc,state.WithFlip(PsdFlipState.X).SetVisible(0,true)).SequenceEqual([0,4,5]));
            check(Active(doc,state.WithFlip(PsdFlipState.XY).SetVisible(0,true)).SequenceEqual([0,10,11,12,13,15]));
            var shown=state.WithFlip(PsdFlipState.None).SetVisible(6,true).SetVisible(0,true);
            check(Active(doc,shown).SequenceEqual([0,1,2,3]));
            check(Active(doc,shown.WithFlip(PsdFlipState.Y)).SequenceEqual([0,4,5,6,7,9]));
            check(ReferenceEquals(shown,shown.SetVisible(7,false)));
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-direction-nested-internal-flip-uses-origin-and-preserves-radio-snapshots",()=>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,new(null,"body",true,Group:true),new(0,"common",true),
                new(null,"body:flipx",false,Group:true),new(2,"common",true),new(2,"only-group",true,Group:true),
                new(4,"*part",true),new(4,"*part:flipx",false),new(4,"*other",false),new(4,"!leaf",false)));
            var original=PsdVisibilityState.Create(doc.Notation);
            check(original.Bindings.Selection.OriginNodeIds[6]==5 && original.Bindings.Selection.DirectionOnlyScopes[5]==PsdDirectionScope.X);
            check(Active(doc,original.WithFlip(PsdFlipState.X)).SequenceEqual([2,3,4,6,8]));
            var changed=original.WithFlip(PsdFlipState.X).SetVisible(7,true);
            check(Active(doc,changed).SequenceEqual([2,3,4,7,8]));
            check(Active(doc,changed.WithFlip(PsdFlipState.None)).SequenceEqual([0,1]));
            check(changed.WithFlip(PsdFlipState.None).WithFlip(PsdFlipState.X).EnabledNodeIds.SequenceEqual(changed.EnabledNodeIds));
            check(Active(doc,changed.SetVisible(5,true)).SequenceEqual([2,3,4,6,8]));
            Reject(()=>changed.SetVisible(6,true),check);
            check(Active(doc,changed.SetVisible(0,false)).IsEmpty);
            check(Active(doc,changed.SetVisible(0,false).WithFlip(PsdFlipState.None).WithFlip(PsdFlipState.X).SetVisible(0,true)).SequenceEqual([2,3,4,7,8]));
            check(!original.IsLocallyVisible(7) && pool.Snapshot().BlockReadCount==0);
        });
        test("C-direction-root-radio-domains-normal-fallback-and-independent-memory",()=>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var doc=pool.Acquire(Store(root,new(null,"*base",false),new(null,"*x-only:flipx",true),
                new(null,"*y-only:flipy",false),new(null,"*xy-only:flipxy",false),new(null,"*both:flipx:flipy",false)));
            var original=PsdVisibilityState.Create(doc.Notation);
            check(Active(doc,original).SequenceEqual([0]) && Active(doc,original.WithFlip(PsdFlipState.X)).SequenceEqual([1]));
            var state=original.WithFlip(PsdFlipState.Y).SetVisible(2,true).WithFlip(PsdFlipState.XY).SetVisible(3,true);
            foreach(var row in new[]{(PsdFlipState.None,0),(PsdFlipState.X,1),(PsdFlipState.Y,2),(PsdFlipState.XY,3)})
                check(Active(doc,state.WithFlip(row.Item1)).SequenceEqual([row.Item2]));
            var common=state.WithFlip(PsdFlipState.Y).SetVisible(0,true);
            check(Active(doc,common).SequenceEqual([0]) && !common.IsLocallyVisible(1) && !common.IsLocallyVisible(3));
            foreach(var flip in new[]{PsdFlipState.None,PsdFlipState.X,PsdFlipState.Y,PsdFlipState.XY})
                check(Active(doc,common.WithFlip(flip)).SequenceEqual([0]));
            var shared=state.WithFlip(PsdFlipState.Y).SetVisible(4,true);
            foreach(var row in new[]{(PsdFlipState.None,0),(PsdFlipState.X,4),(PsdFlipState.Y,4),(PsdFlipState.XY,3)})
                check(Active(doc,shared.WithFlip(row.Item1)).SequenceEqual([row.Item2]));
            check(shared.RadioSelectionScopes[4]==(PsdDirectionScope.X|PsdDirectionScope.Y));
            check(!shared.IsLocallyVisible(1) && !shared.IsLocallyVisible(2) && shared.IsLocallyVisible(3));
            var reverse=shared.WithFlip(PsdFlipState.X).SetVisible(1,true);
            check(reverse.RadioSelectionScopes[4]==PsdDirectionScope.Y && reverse.RadioSelectionScopes[1]==PsdDirectionScope.X);
            foreach(var row in new[]{(PsdFlipState.None,0),(PsdFlipState.X,1),(PsdFlipState.Y,4),(PsdFlipState.XY,3)})
                check(Active(doc,reverse.WithFlip(row.Item1)).SequenceEqual([row.Item2]));
            var selectedAgain=reverse.WithFlip(PsdFlipState.Y).SetVisible(4,true);
            check(selectedAgain.RadioSelectionScopes[4]==(PsdDirectionScope.X|PsdDirectionScope.Y));
            check(Active(doc,selectedAgain.WithFlip(PsdFlipState.X)).SequenceEqual([4]) && !selectedAgain.IsLocallyVisible(1));
            check(ReferenceEquals(selectedAgain.RadioSelectionScopes,selectedAgain.WithFlip(PsdFlipState.XY).RadioSelectionScopes));
            check(Active(doc,reverse.WithFlip(PsdFlipState.X)).SequenceEqual([1]));
            check(Active(doc,shared.WithFlip(PsdFlipState.X)).SequenceEqual([4]));
            check(state.IsLocallyVisible(1) && state.IsLocallyVisible(2) && !state.IsLocallyVisible(4));
            using var mixed=pool.Acquire(Store(root,new(null,"*base",false),new(null,"*x-only:flipx",false),
                new(null,"*y-only:flipy",false),new(null,"*xy-only:flipxy",false),new(null,"*both:flipx:flipy",true)));
            var selected=PsdVisibilityState.Create(mixed.Notation).WithFlip(PsdFlipState.Y);
            var replaced=selected.SetVisible(0,true);
            check(!replaced.IsLocallyVisible(4) && Active(mixed,replaced.WithFlip(PsdFlipState.X)).SequenceEqual([0]));
            check(Active(mixed,selected).SequenceEqual([4]) && Active(mixed,selected.WithFlip(PsdFlipState.X)).SequenceEqual([4]));
            check(Active(doc,state.WithFlip(PsdFlipState.Y)).SequenceEqual([2]) && pool.Snapshot().BlockReadCount==0);
        });
        test("C-radio-priority-partial-common-scope-and-P2-alias-outside-edit-range",()=>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var doc=pool.Acquire(Store(root,new(null,"body",true,Group:true),
                new(0,"*common",true),new(0,"*other",false),new(null,"body:flipx",false,Group:true),
                new(3,"*common",true),new(3,"*X-only",false)));
            var initial=PsdVisibilityState.Create(doc.Notation);
            var otherScope=PsdDirectionScope.Unflipped|PsdDirectionScope.Y|PsdDirectionScope.XY;
            check(initial.Bindings.Selection.SelectionScopes[2]==otherScope);
            var directional=initial.WithFlip(PsdFlipState.X).SetVisible(5,true);
            var edited=directional.WithFlip(PsdFlipState.Y).SetVisible(2,true);
            foreach(var flip in new[]{PsdFlipState.None,PsdFlipState.Y,PsdFlipState.XY})
                check(Active(doc,edited.WithFlip(flip)).SequenceEqual([0,2]));
            check(Active(doc,edited.WithFlip(PsdFlipState.X)).SequenceEqual([3,5]));
            check(edited.RadioSelectionScopes[5]==PsdDirectionScope.X && edited.RadioSelectionScopes[2]==otherScope);
            var full=edited.SetVisible(1,true);
            check(Active(doc,full.WithFlip(PsdFlipState.X)).SequenceEqual([3,4]));
            check(!full.IsLocallyVisible(5) && Active(doc,full.WithFlip(PsdFlipState.None)).SequenceEqual([0,1]));
            check(Active(doc,edited.WithFlip(PsdFlipState.X)).SequenceEqual([3,5]));
            using var p2=pool.Acquire(Store(root,new(null,"body",false,Group:true),new(0,"*part",false),
                new(0,"*part:flipx",true),new(0,"*Y-only:flipy",false),
                new(null,"body:flipx",true,Group:true),new(4,"*part:flipx",true)));
            var saved=PsdVisibilityState.Create(p2.Notation);
            check(Active(p2,saved).SequenceEqual([0,2]) && !saved.IsLocallyVisible(1));
            var changed=saved.WithFlip(PsdFlipState.Y).SetVisible(3,true);
            check(changed.RadioSelectionScopes[1]==(PsdDirectionScope.All&~PsdDirectionScope.Y));
            check(Active(p2,changed).SequenceEqual([0,3]));
            check(Active(p2,changed.WithFlip(PsdFlipState.None)).SequenceEqual([0,2]));
            check(!changed.IsLocallyVisible(1) && changed.IsLocallyVisible(2));
            check(Active(p2,changed.WithFlip(PsdFlipState.X)).SequenceEqual([4,5]));
            check(Active(p2,saved.WithFlip(PsdFlipState.Y)).SequenceEqual([0,1]));
            check(saved.RadioSelectionScopes[1]==PsdDirectionScope.All && pool.Snapshot().BlockReadCount==0);
        });
        test("C-direction-wide-radio-index-keeps-identities-and-disjoint-direction-choices",()=>
        {
            const int count=512;
            using var pool=new SharedDocumentPool(4096,1);
            using var doc=pool.Acquire(Store(root,Enumerable.Range(0,count).Select(id=>
                new Spec(null,"*d"+id+(id%2==0?":flipx":":flipy"),id==0)).ToArray()));
            var original=PsdVisibilityState.Create(doc.Notation);var index=original.Bindings.Selection;
            check(index.Diagnostics.IsEmpty && index.DirectionOnlyScopes.Count==count);
            check(ReferenceEquals(index,doc.Notation.FlipBindings.Selection));
            check(index.OriginNodeIds.SequenceEqual(Enumerable.Range(0,count)));
            check(Active(doc,original).IsEmpty);
            var changed=original.WithFlip(PsdFlipState.Y).SetVisible(257,true);
            check(Active(doc,changed.WithFlip(PsdFlipState.X)).SequenceEqual([0]));
            check(Active(doc,changed).SequenceEqual([257]));
            check(Active(doc,changed.WithFlip(PsdFlipState.None)).IsEmpty);
            check(!original.IsLocallyVisible(257) && original.IsLocallyVisible(0));
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-direction-ambiguous-origin-radio-ownership-and-empty-scope-reject-with-identities",()=>
        {
            (Spec[] Specs,PsdFlipBindingIssue Issue)[] rows=
            [
                ([new(null,"body",true,Group:true),new(0,"part:flipx",true),new(null,"body:flipx",false,Group:true),
                    new(2,"part",true),new(2,"part:flipx",false)],PsdFlipBindingIssue.AmbiguousSelectionOrigin),
                ([new(null,"body",true,Group:true),new(0,"*common",false),new(0,"*extra:flipx",true),
                    new(null,"body:flipx",false,Group:true),new(3,"*extra:flipx",false),new(3,"*other",true)],PsdFlipBindingIssue.AmbiguousDirectionalRadioGroup),
                ([new(null,"body",true,Group:true),new(0,"common",true),new(null,"body:flipx",false,Group:true),
                    new(2,"common",true),new(2,"extra:flipy",true)],PsdFlipBindingIssue.IncompatibleDirectionScope)
            ];
            foreach(var row in rows)
            {
                using var pool=new SharedDocumentPool(128,1);using var doc=pool.Acquire(Store(root,row.Specs));
                check(doc.Notation.FlipBindings.Diagnostics.Any(d=>d.Issue==row.Issue && d.BlocksPreparation));
                check(doc.Notation.Nodes.Select(n=>n.OriginalName).SequenceEqual(row.Specs.Select(s=>s.Name)));
                check(doc.Notation.FlipBindings.Diagnostics.All(d=>d.NodeId>=0 && d.RelatedNodeIds.All(id=>id>=0 && id<row.Specs.Length)));
                Reject(()=>PsdVisibilityState.Create(doc.Notation),check);
                check(pool.Snapshot().BlockReadCount==0);
            }
        });
    }
    private static System.Collections.Immutable.ImmutableArray<int> Active(SharedDocumentLease doc,PsdVisibilityState state)
        =>RenderPlan.CreateNotationVisibility(doc.Manifest,state).ActiveNodeIds;
    private static string Store(string root,params Spec[] specs)=>FlipCases.Store(root,specs);
    private static void Reject(Action action,Action<bool> check)
    {var rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(PsdFlipBindingException){rejected=true;}check(rejected);}
}
