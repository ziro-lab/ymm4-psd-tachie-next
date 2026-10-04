using System.Collections.Immutable;
using PsdTachieNext.Core;

internal static class FlipCases
{
    internal static void Run(Action<string, Action> test, Action<bool> check, string root)
    {
        FlipSelectionCases.Run(test,check,root);
        DirectionCases.Run(test,check,root);
        test("C-flip-four-states-combined-X-Y-suffix-and-direct-evaluation", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var doc = pool.Acquire(Store(root, new(null,"body",true),
                new(null,"body:flipx:flipy",false), new(null,"body:flipxy",false)));
            var state = PsdVisibilityState.CreateVerified(doc.Notation);
            check(ReferenceEquals(doc.Notation.FlipBindings, doc.Notation.FlipBindings));
            check(doc.Notation.FlipBindings.Diagnostics.IsEmpty && doc.Notation.FlipBindings.Pairs.Length == 2);
            var expected = new Dictionary<PsdFlipState,int> { [PsdFlipState.None]=0,[PsdFlipState.X]=1,[PsdFlipState.Y]=1,[PsdFlipState.XY]=2 };
            var current = state;
            foreach (var flip in new[] { PsdFlipState.X, PsdFlipState.Y, PsdFlipState.XY, PsdFlipState.None, PsdFlipState.Y })
            {
                current = current.WithFlip(flip);
                check(current.EnabledNodeIds.SequenceEqual([expected[flip]]));
                check(current.EnabledNodeIds.SequenceEqual(state.WithFlip(flip).EnabledNodeIds));
                check(current.IsLocallyVisible(0) && !current.IsLocallyVisible(1) && !current.IsLocallyVisible(2));
                var plan = RenderPlan.CreateNotationVisibility(doc.Manifest,current);
                check(plan.RequiredBlockIds.SequenceEqual([expected[flip]]) && plan.FlipState == flip);
            }
            check(ReferenceEquals(state,state.WithFlip(PsdFlipState.None)));
            Reject<ArgumentOutOfRangeException>(() => state.WithFlip((PsdFlipState)99),check);
            Reject<ArgumentException>(() => state.SetVisible(1,true),check);
            check(pool.Snapshot().BlockReadCount == 0);
        });
        test("C-flip-prefix-radio-saved-variant-and-force-visible-normalization", () =>
        {
            using var pool = new SharedDocumentPool(128,1);
            using var doc = pool.Acquire(Store(root,new(null,"*mouth:flipx",true),new(null,"*mouth",true),
                new(null,"*other",false),new(null,"!body",false),new(null,"!body:flipy",false)));
            var state = PsdVisibilityState.CreateVerified(doc.Notation);
            check(state.EnabledNodeIds.SequenceEqual([1,3]));
            check(state.WithFlip(PsdFlipState.X).EnabledNodeIds.SequenceEqual([0,3]));
            check(state.WithFlip(PsdFlipState.Y).EnabledNodeIds.SequenceEqual([1,4]));
            check(state.WithFlip(PsdFlipState.XY).EnabledNodeIds.SequenceEqual([1,3])); // no XY counterpart
            check(ReferenceEquals(state,state.SetVisible(1,false)) && ReferenceEquals(state,state.SetVisible(3,false)));
            var other = state.SetVisible(2,true).WithFlip(PsdFlipState.X);
            check(other.EnabledNodeIds.SequenceEqual([2,3]) && !other.IsLocallyVisible(1));
            check(doc.Manifest.Nodes[0].DefaultVisible && !doc.Manifest.Nodes[3].DefaultVisible);
            check(pool.Snapshot().BlockReadCount == 0);
        });
        test("C-flip-group-duplicate-child-IDs-and-hidden-parent-choice-retention", () =>
        {
            using var pool = new SharedDocumentPool(128,1);
            using var doc = pool.Acquire(Store(root,
                new(null,"parent",false,Group:true),new(0,"body",true,Group:true),
                new(1,"*same",true),new(1,"*same",false),new(1,"nested/%2f",true,Group:true),new(4,"!leaf",false),
                new(0,"body:flipx",false,Group:true),new(6,"*same",false),new(6,"*same",true),
                new(6,"nested/%2f",true,Group:true),new(9,"!leaf",false)));
            var state = PsdVisibilityState.CreateVerified(doc.Notation);
            var pair = state.Bindings.Pairs.Single();
            check(pair.Children.SequenceEqual(new[] {new PsdFlipNodeMap(2,7),new(3,8),new(4,9),new(5,10)}));
            check(doc.Notation.FindOriginalName(1,"*same").SequenceEqual([2,3]));
            check(doc.Notation.FindOriginalName(6,"*same").SequenceEqual([7,8]));
            var switched = state.SetVisible(3,true).WithFlip(PsdFlipState.X);
            check(switched.IsLocallyVisible(3) && !switched.IsLocallyVisible(2));
            check(RenderPlan.CreateNotationVisibility(doc.Manifest,switched).ActiveNodeIds.IsEmpty);
            var shown = switched.SetVisible(0,true);
            var plan = RenderPlan.CreateNotationVisibility(doc.Manifest,shown);
            check(plan.ActiveNodeIds.SequenceEqual([0,6,8,9,10]));
            check(plan.RequiredBlockIds.SequenceEqual([4,5]));
            using (var ready = PreparedAppearanceLease.Prepare(doc,plan))
                check(ready.GetBlock(4).Span[0] == 8 && ready.GetBlock(5).Span[0] == 10);
            var returned = shown.SetVisible(0,false).WithFlip(PsdFlipState.None).SetVisible(0,true);
            check(RenderPlan.CreateNotationVisibility(doc.Manifest,returned).ActiveNodeIds.SequenceEqual([0,1,3,4,5]));
            check(!doc.Manifest.Nodes[3].DefaultVisible && doc.Manifest.Nodes[8].DefaultVisible);
        });
        test("C-flip-binding-diagnostics-never-guess-missing-ambiguous-or-mismatched-identities", () =>
        {
            (Spec[] Specs,PsdFlipBindingIssue Issue)[] rows =
            [
                ([new(null,"body:flipx",false)],PsdFlipBindingIssue.MissingBase),
                ([new(null,"body",true),new(null,"body",false),new(null,"body:flipx",false)],PsdFlipBindingIssue.AmbiguousBase),
                ([new(null,"body",true),new(null,"body:flipx",false),new(null,"body:flipx",false)],PsdFlipBindingIssue.MultipleVariants),
                ([new(null,"body",true,Group:true),new(null,"body:flipx",false)],PsdFlipBindingIssue.KindMismatch),
                ([new(null,"body",true,Group:true),new(0,"one",true),new(null,"body:flipx",false,Group:true),new(2,"two",true)],PsdFlipBindingIssue.MissingChild),
                ([new(null,"body",true,Group:true),new(0,"same",true),new(0,"same",false),new(null,"body:flipx",false,Group:true),new(3,"same",true)],PsdFlipBindingIssue.DuplicateCountMismatch),
                ([new(null,"Body",true),new(null,"body:flipx",false)],PsdFlipBindingIssue.MissingBase)
            ];
            foreach (var row in rows)
            {
                using var pool = new SharedDocumentPool(128,1);using var doc = pool.Acquire(Store(root,row.Specs));
                var binding = doc.Notation.FlipBindings;
                check(binding.Diagnostics.Any(d => d.Issue == row.Issue));
                check(binding.Diagnostics.All(d => d.NodeId >= 0 && d.RelatedNodeIds.All(id => id >= 0 && id < row.Specs.Length)));
                check(doc.Notation.Nodes.Select(n => n.OriginalName).SequenceEqual(row.Specs.Select(s => s.Name)));
                check(pool.Snapshot().BlockReadCount == 0);
                if (row.Issue is PsdFlipBindingIssue.AmbiguousBase or PsdFlipBindingIssue.MultipleVariants or PsdFlipBindingIssue.KindMismatch)
                {
                    check(binding.Pairs.IsEmpty);
                    PsdFlipBindingException? failure=null;
                    try { _=PsdVisibilityState.Create(doc.Notation); } catch(PsdFlipBindingException ex){failure=ex;}
                    check(failure is not null && failure.Diagnostics.All(d=>d.BlocksPreparation));
                    var diagnostic=PreparationDiagnostic.From(failure);
                    check(diagnostic?.Recovery==PreparationRecovery.UnsupportedNotation && !diagnostic.Action.Contains("RGB8"));
                }
                if(row.Issue==PsdFlipBindingIssue.DuplicateCountMismatch)
                    Reject<PsdFlipBindingException>(()=>PsdVisibilityState.Create(doc.Notation),check);
            }
        });
        test("C-flip-reference-missing-base-and-child-retain-state-with-explicit-diagnostics", () =>
        {
            using var pool=new SharedDocumentPool(128,2);
            using var standalone=pool.Acquire(Store(root,new Spec(null,"body:flipx",true)));
            var literal=PsdVisibilityState.Create(standalone.Notation);
            check(literal.Bindings.Pairs.IsEmpty && literal.Bindings.Diagnostics.Single().Issue==PsdFlipBindingIssue.MissingBase);
            check(!literal.Bindings.Diagnostics.Single().BlocksPreparation);
            check(literal.EnabledNodeIds.IsEmpty && literal.WithFlip(PsdFlipState.X).EnabledNodeIds.SequenceEqual([0]));
            check(literal.SetVisible(0,false).EnabledNodeIds.IsEmpty);
            using var group=pool.Acquire(Store(root,new(null,"body",true,Group:true),new(0,"shared",false),new(0,"left-only",true),
                new(null,"body:flipx",false,Group:true),new(3,"shared",true),new(3,"right-only",true)));
            var state=PsdVisibilityState.Create(group.Notation);var flipped=state.WithFlip(PsdFlipState.X);
            check(state.Bindings.Diagnostics.Select(d=>d.NodeId).Order().SequenceEqual([2,5]));
            check(state.Bindings.Diagnostics.All(d=>d.Issue==PsdFlipBindingIssue.MissingChild && !d.BlocksPreparation));
            check(RenderPlan.CreateNotationVisibility(group.Manifest,flipped).ActiveNodeIds.SequenceEqual([3,5]));
            check(flipped.IsLocallyVisible(2) && !flipped.IsLocallyVisible(1));
            check(RenderPlan.CreateNotationVisibility(group.Manifest,flipped.SetVisible(1,true)).ActiveNodeIds.SequenceEqual([3,4,5]));
            check(flipped.WithFlip(PsdFlipState.None).EnabledNodeIds.SequenceEqual(state.EnabledNodeIds));
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-flip-initial-None-normalizes-once-with-missing-nested-base", () =>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,new(null,"body",false,Group:true),new(0,"part",false),new(0,"part:flipx",true),
                new(null,"body:flipx",true,Group:true),new(3,"part:flipx",true)));
            var state=PsdVisibilityState.Create(doc.Notation);
            var plan=RenderPlan.CreateNotationVisibility(doc.Manifest,state);
            check(plan.ActiveNodeIds.SequenceEqual([0,1,2]));
            check(state.EnabledNodeIds.SequenceEqual([0,1,2,4]));
            check(state.Bindings.Diagnostics.Any(d=>d.Issue==PsdFlipBindingIssue.MissingBase && d.NodeId==4));
            check(state.Bindings.Diagnostics.All(d=>!d.BlocksPreparation));
            check(state.IsLocallyVisible(1) && state.IsLocallyVisible(2));
            check(ReferenceEquals(state,state.WithFlip(PsdFlipState.None)));
            // Oracles are fresh initialization + one setter at pinned reference 5f40b67.
            foreach(var flip in new[]{PsdFlipState.X,PsdFlipState.Y,PsdFlipState.XY})
            {
                var changed=state.WithFlip(flip);
                var expected=flip==PsdFlipState.X ? new[]{3,4} : new[]{0,1};
                check(RenderPlan.CreateNotationVisibility(doc.Manifest,changed).ActiveNodeIds.SequenceEqual(expected));
                check(changed.WithFlip(PsdFlipState.None).EnabledNodeIds.SequenceEqual(state.EnabledNodeIds));
                check(changed.IsLocallyVisible(2));
            }
            // Pure None restores the normalized snapshot; repeated reference setters can be non-idempotent.
            check(state.SetVisible(1,false).EnabledNodeIds.SequenceEqual([0]));
            check(pool.Snapshot().BlockReadCount==0);
        });
        test("C-flip-nested-pairs-evaluate-children-before-group-transfer", () =>
        {
            using var pool=new SharedDocumentPool(128,1);
            using var doc=pool.Acquire(Store(root,new(null,"body",true,Group:true),new(0,"part",true),new(0,"part:flipx",false),
                new(null,"body:flipx",false,Group:true),new(3,"part",false),new(3,"part:flipx",true)));
            var state=PsdVisibilityState.Create(doc.Notation);
            check(state.Bindings.Pairs.Select(p=>p.FlippedId).SequenceEqual([2,5,3]));
            check(RenderPlan.CreateNotationVisibility(doc.Manifest,state).ActiveNodeIds.SequenceEqual([0,1]));
            var flipped=state.WithFlip(PsdFlipState.X);
            check(RenderPlan.CreateNotationVisibility(doc.Manifest,flipped).ActiveNodeIds.SequenceEqual([3,5]));
            check(RenderPlan.CreateNotationVisibility(doc.Manifest,flipped).RequiredBlockIds.SequenceEqual([3]));
            check(RenderPlan.CreateNotationVisibility(doc.Manifest,state.WithFlip(PsdFlipState.Y)).ActiveNodeIds.SequenceEqual([0,1]));
            check(flipped.WithFlip(PsdFlipState.None).EnabledNodeIds.SequenceEqual(state.EnabledNodeIds));
            check(state.IsLocallyVisible(1) && !state.IsLocallyVisible(2));
        });
    }

    private static void Reject<T>(Action action,Action<bool> check) where T:Exception
    {var rejected=false;try{action();}catch(T){rejected=true;}check(rejected);}
    internal sealed record Spec(int? Parent,string Name,bool Visible,bool Group=false,uint? PsdId=null);
    internal static string Store(string root,params Spec[] specs)
    {
        var dir=Path.Combine(root,Guid.NewGuid().ToString("N"));using var writer=new CompiledStoreWriter(dir);
        var orders=new Dictionary<int,int>();
        var nodes=specs.Select((s,id)=>
        {
            var parent=s.Parent??-1;var order=orders.GetValueOrDefault(parent);orders[parent]=order+1;
            int? block=s.Group?null:writer.AddBlock(BlockFormat.Bgra8Straight,1,1,new byte[]{(byte)id,0,0,255},compress:false);
            var tags=ImmutableArray<ExtraTag>.Empty;
            if(s.PsdId is uint stable)
            {
                var bytes=new byte[4];System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes,stable);
                tags=[new ExtraTag("lyid",writer.AddBlock(BlockFormat.Metadata,0,0,bytes,compress:false))];
            }
            return new LayerNode(id,s.Parent,order,s.Group?NodeKind.Group:NodeKind.Layer,s.Name,new(0,0,1,1),
                s.Visible?(byte)0:(byte)2,s.Visible,s.Group?"pass":"norm",255,false,block,null,tags);
        }).ToImmutableArray();
        var source=new SourceFingerprint(CompiledFormat.Hash(System.Text.Encoding.UTF8.GetBytes(dir)),26);
        writer.Complete(dir,new(1,CompiledFormat.CompilerId,CompiledFormat.Generation(source),source,1,1,1,8,3,null,null,nodes,writer.Blocks));return dir;
    }
}
