using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PsdTachieNext.Core;
using PsdTachieNext.Direct2D;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

var results = new List<object>(); var failed = 0; var assertions = 0;
var root = Path.Combine(Path.GetTempPath(), "psd-next-tree-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var d3d = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, Vortice.Direct3D.FeatureLevel.Level_11_0);
    using var dxgi = d3d.QueryInterface<IDXGIDevice>();
    using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
    using var device = factory.CreateDevice(dxgi);
    using var context = device.CreateDeviceContext(DeviceContextOptions.None);

    Case("C-prefix-prepared-radio-switch-and-hidden-parent-no-recompose", () =>
    {
        var dir = Save([G(S(0,0,255) with { Name="*same" }, S(0,255,0) with { Name="*same",Visible=false })
            with { Visible=false }, S(255,0,0) with { Name="!backdrop",Visible=false }]);
        using var pool = new SharedDocumentPool(4096, 1); using var source = pool.Acquire(dir);
        var state = PsdPrefixVisibility.Create(source.Notation);
        using var first = PreparedAppearanceLease.Prepare(source, RenderPlan.CreatePrefixVisibility(source.Manifest,state));
        using var renderer = new TreeCompiledRenderer(context,first);
        Check(renderer.UpdatePrepared(first)); Bytes([255,0,0,255], renderer.Readback());
        var pointer = renderer.Output!.NativePointer; var reads = pool.Snapshot().BlockReadCount;
        state = state.SetVisible(2,true);
        using (var hidden = PreparedAppearanceLease.Prepare(source, RenderPlan.CreatePrefixVisibility(source.Manifest,state)))
        {
            Check(!renderer.UpdatePrepared(hidden)); Check(renderer.Output.NativePointer==pointer);
            Check(pool.Snapshot().BlockReadCount==reads && renderer.CompositionCount==1);
        }
        state = state.SetVisible(0,true);
        using (var green = PreparedAppearanceLease.Prepare(source, RenderPlan.CreatePrefixVisibility(source.Manifest,state)))
        { Check(renderer.UpdatePrepared(green)); Bytes([0,255,0,255], renderer.Readback()); }
        state = state.SetVisible(1,true);
        using (var red = PreparedAppearanceLease.Prepare(source, RenderPlan.CreatePrefixVisibility(source.Manifest,state)))
        { Check(renderer.UpdatePrepared(red)); Bytes([0,0,255,255], renderer.Readback()); }
        Check(renderer.CompositionCount==3 && pool.Snapshot().BlockReadCount==3);
        Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
    });
    Case("C-flip-whole-canvas-four-states-key-and-context-restoration", () =>
    {
        byte[] red=[0,0,255,255],green=[0,255,0,255],blue=[255,0,0,255],white=[255,255,255,255];
        byte[] Grid(params byte[][] pixels)=>pixels.SelectMany(p=>p).ToArray();
        var dir=Save([new(Pixels:Grid(red,green,blue,white),Bounds:new(0,0,2,2),Name:"plain")],2,2);
        using var pool=new SharedDocumentPool(4096,1);using var source=pool.Acquire(dir);
        var state=PsdVisibilityState.Create(source.Notation);
        using var initial=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state));
        using var renderer=new TreeCompiledRenderer(context,initial);
        var sentinel=Matrix3x2.CreateTranslation(7,9);context.Transform=sentinel;
        try
        {
            foreach(var row in new[] {
                (PsdFlipState.None,Grid(red,green,blue,white)),(PsdFlipState.X,Grid(green,red,white,blue)),
                (PsdFlipState.Y,Grid(blue,white,red,green)),(PsdFlipState.XY,Grid(white,blue,green,red))})
            {
                using var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state.WithFlip(row.Item1)));
                Check(renderer.UpdatePrepared(ready));Bytes(row.Item2,renderer.Readback());Check(context.Transform==sentinel);
                var pointer=renderer.Output!.NativePointer;Check(!renderer.UpdatePrepared(ready));Check(renderer.Output.NativePointer==pointer);
            }
            Check(renderer.CompositionCount==4 && pool.Snapshot().BlockReadCount==1);
            Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
        } finally {context.Transform=Matrix3x2.Identity;}
    });
    Case("C-flip-counterpart-X-Y-shared-suffix-and-separate-XY-pixels", () =>
    {
        byte[] red=[0,0,255,255],green=[0,255,0,255],blue=[255,0,0,255],white=[255,255,255,255];
        byte[] Grid(params byte[][] pixels)=>pixels.SelectMany(p=>p).ToArray();
        var dir=Save([new(Pixels:Grid(red,green,blue,white),Bounds:new(0,0,2,2),Name:"body"),
            new(Pixels:Grid(blue,red,white,green),Bounds:new(0,0,2,2),Name:"body:flipx:flipy",Visible:false),
            new(Pixels:Grid(green,white,red,blue),Bounds:new(0,0,2,2),Name:"body:flipxy",Visible:false)],2,2);
        using var pool=new SharedDocumentPool(4096,1);using var source=pool.Acquire(dir);
        var state=PsdVisibilityState.Create(source.Notation);
        using var initial=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state));
        using var renderer=new TreeCompiledRenderer(context,initial);
        foreach(var row in new[] {(PsdFlipState.None,Grid(red,green,blue,white)),(PsdFlipState.X,Grid(red,blue,green,white)),
            (PsdFlipState.Y,Grid(white,green,blue,red)),(PsdFlipState.XY,Grid(blue,red,white,green))})
        {
            using var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state.WithFlip(row.Item1)));
            Check(renderer.UpdatePrepared(ready));Bytes(row.Item2,renderer.Readback());
        }
        Check(renderer.CompositionCount==4 && pool.Snapshot().BlockReadCount==3);
        Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
    });
    Case("C-flip-group-prefix-duplicate-radio-choice-survives-hidden-parent", () =>
    {
        var dir=Save([G(G(S(0,0,255) with {Name="*same"},S(0,255,0) with {Name="*same",Visible=false}) with {Name="!body",Visible=false},
            G(S(255,0,0) with {Name="*same",Visible=false,Bounds=new(1,0,1,1)},
                S(0,255,255) with {Name="*same",Bounds=new(1,0,1,1)}) with {Name="!body:flipx",Visible=false}) with {Name="parent",Visible=false}],2);
        using var pool=new SharedDocumentPool(4096,1);using var source=pool.Acquire(dir);
        var state=PsdVisibilityState.Create(source.Notation).SetVisible(2,true).WithFlip(PsdFlipState.X);
        using var initial=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state));
        using var renderer=new TreeCompiledRenderer(context,initial);
        Check(renderer.UpdatePrepared(initial));Bytes([0,0,0,0,0,0,0,0],renderer.Readback());
        Check(pool.Snapshot().BlockReadCount==0 && state.IsLocallyVisible(2));
        state=state.SetVisible(0,true);
        using(var blueReady=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state)))
        {Check(renderer.UpdatePrepared(blueReady));Bytes([255,0,0,255,0,0,0,0],renderer.Readback());}
        state=state.SetVisible(3,true);
        using(var yellowReady=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state)))
        {Check(renderer.UpdatePrepared(yellowReady));Bytes([0,255,255,255,0,0,0,0],renderer.Readback());}
        using(var greenReady=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state.WithFlip(PsdFlipState.None))))
        {Check(renderer.UpdatePrepared(greenReady));Bytes([0,255,0,255,0,0,0,0],renderer.Readback());}
        Check(renderer.CompositionCount==4 && pool.Snapshot().BlockReadCount==3);
        Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
    });
    Case("C-flip-initial-None-missing-nested-base-normalizes-once-pixels", () =>
    {
        var dir=Save([G(S(0,0,255) with {Name="part",Visible=false},
                S(0,255,0) with {Name="part:flipx",Bounds=new(1,0,1,1)})
                with {Name="body",Visible=false,Bounds=new(0,0,2,1)},
            G(S(255,0,0) with {Name="part:flipx",Bounds=new(1,0,1,1)})
                with {Name="body:flipx",Bounds=new(0,0,2,1)}],2);
        using var pool=new SharedDocumentPool(4096,1);using var source=pool.Acquire(dir);
        var state=PsdVisibilityState.Create(source.Notation);
        using var initial=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state));
        using var renderer=new TreeCompiledRenderer(context,initial);
        byte[] both=[0,0,255,255,0,255,0,255],redLeft=[0,0,255,255,0,0,0,0];
        foreach(var row in new[]{(PsdFlipState.None,both),(PsdFlipState.X,new byte[]{255,0,0,255,0,0,0,0}),
            (PsdFlipState.None,both),(PsdFlipState.Y,redLeft),(PsdFlipState.None,both),
            (PsdFlipState.XY,new byte[]{0,0,0,0,0,0,255,255}),(PsdFlipState.None,both)})
        {
            using var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state.WithFlip(row.Item1)));
            Check(renderer.UpdatePrepared(ready));Bytes(row.Item2,renderer.Readback());
        }
        Check(renderer.CompositionCount==7 && pool.Snapshot().BlockReadCount==3);
        Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
    });
    Case("C-direction-initial-common-radio-alias-keeps-exclusive-pixels-and-roundtrips", () =>
    {
        var dir=Save([G(S(0,0,255) with {Name="*part",Visible=false},
                S(0,255,0) with {Name="*part:flipx",Bounds=new(1,0,1,1)})
                with {Name="body",Visible=false,Bounds=new(0,0,2,1)},
            G(S(255,0,0) with {Name="*part:flipx",Bounds=new(1,0,1,1)})
                with {Name="body:flipx",Bounds=new(0,0,2,1)}],2);
        using var pool=new SharedDocumentPool(4096,1);using var source=pool.Acquire(dir);
        var state=PsdVisibilityState.Create(source.Notation);
        Check(state.EnabledNodeIds.SequenceEqual([0,2,4]));
        using var initial=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state));
        using var renderer=new TreeCompiledRenderer(context,initial);
        byte[] greenRight=[0,0,0,0,0,255,0,255],redLeft=[0,0,255,255,0,0,0,0];
        var current=state;
        foreach(var row in new[]{(PsdFlipState.None,greenRight),(PsdFlipState.X,new byte[]{255,0,0,255,0,0,0,0}),
            (PsdFlipState.None,greenRight),(PsdFlipState.Y,redLeft),(PsdFlipState.None,greenRight),
            (PsdFlipState.XY,new byte[]{0,0,0,0,0,0,255,255}),(PsdFlipState.None,greenRight)})
        {
            current=current.WithFlip(row.Item1);
            using var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,current));
            Check(renderer.UpdatePrepared(ready));Bytes(row.Item2,renderer.Readback());
            Check(!current.IsLocallyVisible(1) && current.IsLocallyVisible(2));
        }
        var edited=state.WithFlip(PsdFlipState.X).SetVisible(1,true).WithFlip(PsdFlipState.None);
        using(var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,edited)))
        {Check(renderer.UpdatePrepared(ready));Bytes(redLeft,renderer.Readback());}
        using(var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state)))
        {Check(renderer.UpdatePrepared(ready));Bytes(greenRight,renderer.Readback());}
        Check(renderer.CompositionCount==9 && pool.Snapshot().BlockReadCount==3);
        Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
    });
    Case("C-flip-edited-expression-returns-latest-normal-choice-and-snapshot-pixels", () =>
    {
        var dir=Save([G(S(0,0,255) with {Name="*same"},S(0,255,0) with {Name="*same",Visible=false})
                with {Name="body",Bounds=new(0,0,2,2)},
            G(S(255,0,0) with {Name="*same",Bounds=new(1,0,1,1)},
                S(0,255,255) with {Name="*same",Visible=false,Bounds=new(1,0,1,1)})
                with {Name="body:flipx",Visible=false,Bounds=new(0,0,2,2)},
            G(S(255,255,255) with {Name="*same",Bounds=new(0,1,1,1)},
                S(255,0,255) with {Name="*same",Visible=false,Bounds=new(0,1,1,1)})
                with {Name="body:flipy",Visible=false,Bounds=new(0,0,2,2)},
            G(S(0,0,255) with {Name="*same",Bounds=new(1,1,1,1)},
                S(255,255,0) with {Name="*same",Visible=false,Bounds=new(1,1,1,1)})
                with {Name="body:flipxy",Visible=false,Bounds=new(0,0,2,2)}],2,2);
        using var pool=new SharedDocumentPool(4096,1);using var source=pool.Acquire(dir);
        var original=PsdVisibilityState.CreateVerified(source.Notation);
        using var initial=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,original));
        using var renderer=new TreeCompiledRenderer(context,initial);
        byte[] Pixel(byte b,byte g,byte r)=>[b,g,r,255,0,0,0,0,0,0,0,0,0,0,0,0];
        foreach(var row in new[]{(PsdFlipState.X,Pixel(255,0,0),Pixel(0,255,255)),
            (PsdFlipState.Y,Pixel(255,255,255),Pixel(255,0,255)),(PsdFlipState.XY,Pixel(0,0,255),Pixel(255,255,0))})
        {
            var before=original.WithFlip(row.Item1);var edited=before.SetVisible(2,true);
            var returned=edited.WithFlip(PsdFlipState.None);
            foreach(var snapshot in new[]{(before,row.Item2),(edited,row.Item3),(returned,Pixel(0,255,0)),
                (before,row.Item2),(edited,row.Item3),(returned,Pixel(0,255,0))})
            {
                using var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,snapshot.Item1));
                Check(renderer.UpdatePrepared(ready));Bytes(snapshot.Item2,renderer.Readback());
            }
            Check(before.IsLocallyVisible(1) && edited.IsLocallyVisible(2));
        }
        Check(renderer.CompositionCount==18 && pool.Snapshot().BlockReadCount==8);
        Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
    });
    Case("C-direction-only-root-force-memory-scopes-hidden-parent-and-pixels", () =>
    {
        var dir=Save([G(S(255,255,255) with {Name="base"},
            S(0,0,255) with {Name="!shine:flipx",Visible=false,Bounds=new(1,0,1,1)},
            S(255,0,0) with {Name="cape:flipy",Visible=false,Bounds=new(0,1,1,1)},
            S(0,255,0) with {Name="pose:flipxy",Visible=false,Bounds=new(1,1,1,1)})
            with {Name="parent",Visible=false,Bounds=new(0,0,2,2)}],2,2);
        using var pool=new SharedDocumentPool(4096,1);using var source=pool.Acquire(dir);
        var state=PsdVisibilityState.Create(source.Notation);
        using var initial=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state));
        using var renderer=new TreeCompiledRenderer(context,initial);
        Check(renderer.UpdatePrepared(initial));Bytes(new byte[16],renderer.Readback());
        var pointer=renderer.Output!.NativePointer;
        state=state.SetVisible(3,true).SetVisible(4,true);
        using(var hidden=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state)))
        {Check(!renderer.UpdatePrepared(hidden));Check(renderer.Output.NativePointer==pointer && pool.Snapshot().BlockReadCount==0);}
        byte[] clear=[0,0,0,0],white=[255,255,255,255],red=[0,0,255,255],blue=[255,0,0,255],green=[0,255,0,255];
        byte[] Grid(params byte[][] p)=>p.SelectMany(v=>v).ToArray();
        state=state.SetVisible(0,true);
        foreach(var row in new[]{(PsdFlipState.None,Grid(white,clear,clear,clear)),(PsdFlipState.X,Grid(red,white,clear,clear)),
            (PsdFlipState.Y,Grid(blue,clear,white,clear)),(PsdFlipState.XY,Grid(green,clear,clear,white)),
            (PsdFlipState.None,Grid(white,clear,clear,clear))})
        {
            using var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,state.WithFlip(row.Item1)));
            Check(renderer.UpdatePrepared(ready));Bytes(row.Item2,renderer.Readback());
            Check(state.IsLocallyVisible(2) && state.IsLocallyVisible(3) && state.IsLocallyVisible(4));
        }
        var snapshot=state.WithFlip(PsdFlipState.X).SetVisible(0,false);
        using(var hidden=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,snapshot)))
        {Check(renderer.UpdatePrepared(hidden));Bytes(new byte[16],renderer.Readback());}
        using(var restored=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,snapshot.SetVisible(0,true))))
        {Check(renderer.UpdatePrepared(restored));Bytes(Grid(red,white,clear,clear),renderer.Readback());}
        Check(renderer.CompositionCount==8 && pool.Snapshot().BlockReadCount==4);
        Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
    });
    Case("C-direction-radio-survives-transfer-common-return-and-snapshot-pixels", () =>
    {
        var dir=Save([G(S(0,0,255) with {Name="*common"},S(0,255,0) with {Name="*other",Visible=false}) with {Name="body"},
            G(S(0,255,255) with {Name="*common"},S(255,255,0) with {Name="*other",Visible=false},
                S(255,0,0) with {Name="*extra",Visible=false}) with {Name="body:flipx",Visible=false},
            G(S(0,255,255) with {Name="*common"},S(255,255,0) with {Name="*other",Visible=false},
                S(255,0,255) with {Name="*extra",Visible=false}) with {Name="body:flipy",Visible=false},
            G(S(0,255,255) with {Name="*common"},S(255,255,0) with {Name="*other",Visible=false},
                S(255,255,255) with {Name="*extra",Visible=false}) with {Name="body:flipxy",Visible=false}]);
        using var pool=new SharedDocumentPool(4096,1);using var source=pool.Acquire(dir);
        var original=PsdVisibilityState.Create(source.Notation);
        using var initial=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,original));
        using var renderer=new TreeCompiledRenderer(context,initial);
        Check(renderer.UpdatePrepared(initial));Bytes([0,0,255,255],renderer.Readback());
        var latent=original.SetVisible(6,true);
        using(var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,latent)))
        {Check(!renderer.UpdatePrepared(ready));Check(renderer.CompositionCount==1 && pool.Snapshot().BlockReadCount==1);}
        var remembered=latent.WithFlip(PsdFlipState.Y).SetVisible(10,true).WithFlip(PsdFlipState.XY).SetVisible(14,true);
        var edited=remembered.WithFlip(PsdFlipState.X).SetVisible(2,true);
        var clearY=edited.WithFlip(PsdFlipState.Y).SetVisible(2,true);
        byte[] red=[0,0,255,255],green=[0,255,0,255],blue=[255,0,0,255],magenta=[255,0,255,255],white=[255,255,255,255],cyan=[255,255,0,255];
        foreach(var row in new[]{(remembered.WithFlip(PsdFlipState.X),blue),(remembered.WithFlip(PsdFlipState.Y),magenta),
            (remembered.WithFlip(PsdFlipState.XY),white),(remembered.WithFlip(PsdFlipState.None),red),
            (edited,cyan),(edited.WithFlip(PsdFlipState.None),green),(edited.WithFlip(PsdFlipState.Y),cyan),
            (edited.WithFlip(PsdFlipState.XY),cyan),(clearY,cyan),(clearY.WithFlip(PsdFlipState.None),green),
            (remembered.WithFlip(PsdFlipState.X),blue),(remembered.WithFlip(PsdFlipState.None),red),
            (edited,cyan),(clearY.WithFlip(PsdFlipState.None),green)})
        {
            using var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,row.Item1));
            Check(renderer.UpdatePrepared(ready));Bytes(row.Item2,renderer.Readback());
        }
        Check(renderer.CompositionCount==15 && pool.Snapshot().BlockReadCount==8);
        Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
    });
    Case("C-radio-priority-shared-B-reverse-A-hidden-parent-scope-and-snapshot-pixels", () =>
    {
        var dir=Save([G(S(255,255,255) with {Name="*base",Visible=false},
            S(0,0,255) with {Name="*A:flipx"},S(0,255,0) with {Name="*C:flipy",Visible=false},
            S(255,0,0) with {Name="*D:flipxy",Visible=false},
            S(0,255,255) with {Name="*B:flipx:flipy",Visible=false}) with {Name="parent"}]);
        using var pool=new SharedDocumentPool(4096,1);using var source=pool.Acquire(dir);
        var original=PsdVisibilityState.Create(source.Notation);
        var remembered=original.WithFlip(PsdFlipState.Y).SetVisible(3,true).WithFlip(PsdFlipState.XY).SetVisible(4,true);
        var shared=remembered.WithFlip(PsdFlipState.Y).SetVisible(5,true);
        var reverse=shared.WithFlip(PsdFlipState.X).SetVisible(2,true);
        Check(reverse.RadioSelectionScopes[5]==PsdDirectionScope.Y);
        Check(shared.RadioSelectionScopes[5]==(PsdDirectionScope.X|PsdDirectionScope.Y));
        byte[] white=[255,255,255,255],red=[0,0,255,255],green=[0,255,0,255],blue=[255,0,0,255],yellow=[0,255,255,255];
        using var initial=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,original));
        using var renderer=new TreeCompiledRenderer(context,initial);
        foreach(var row in new[]{(original,white),(original.WithFlip(PsdFlipState.X),red),
            (remembered.WithFlip(PsdFlipState.Y),green),(remembered.WithFlip(PsdFlipState.XY),blue),
            (remembered.WithFlip(PsdFlipState.None),white),(shared.WithFlip(PsdFlipState.X),yellow),
            (shared.WithFlip(PsdFlipState.Y),yellow),(shared.WithFlip(PsdFlipState.XY),blue),
            (shared.WithFlip(PsdFlipState.None),white),(reverse.WithFlip(PsdFlipState.X),red),
            (reverse.WithFlip(PsdFlipState.Y),yellow),(reverse.WithFlip(PsdFlipState.XY),blue),
            (reverse.WithFlip(PsdFlipState.None),white)})
        {
            using var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,row.Item1));
            Check(renderer.UpdatePrepared(ready));Bytes(row.Item2,renderer.Readback());
        }
        var hidden=reverse.WithFlip(PsdFlipState.Y).SetVisible(0,false);
        Check(ReferenceEquals(hidden.RadioSelectionScopes,reverse.RadioSelectionScopes));
        using(var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,hidden)))
        {Check(renderer.UpdatePrepared(ready));Bytes([0,0,0,0],renderer.Readback());}
        var hiddenShared=hidden.SetVisible(5,true);
        using(var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,hiddenShared)))
        {Check(!renderer.UpdatePrepared(ready));Check(renderer.CompositionCount==14 && pool.Snapshot().BlockReadCount==5);}
        var shown=hiddenShared.SetVisible(0,true);
        foreach(var row in new[]{(shown.WithFlip(PsdFlipState.X),yellow),(shown.WithFlip(PsdFlipState.Y),yellow),
            (shown.WithFlip(PsdFlipState.XY),blue),(shown.WithFlip(PsdFlipState.None),white),
            (reverse.WithFlip(PsdFlipState.X),red),(shared.WithFlip(PsdFlipState.X),yellow),
            (remembered.WithFlip(PsdFlipState.Y),green)})
        {
            using var ready=PreparedAppearanceLease.Prepare(source,RenderPlan.CreateNotationVisibility(source.Manifest,row.Item1));
            Check(renderer.UpdatePrepared(ready));Bytes(row.Item2,renderer.Readback());
        }
        Check(reverse.RadioSelectionScopes[5]==PsdDirectionScope.Y && hiddenShared.RadioSelectionScopes[5]==(PsdDirectionScope.X|PsdDirectionScope.Y));
        Check(renderer.CompositionCount==21 && pool.Snapshot().BlockReadCount==5);
        Check(renderer.LiveGraphObjects==0 && renderer.LiveTemporaryBitmaps==0);
    });
    Pixels("flat-source-over", [S(0,0,255,128), S(255,0,0)], [127,0,128,255]);
    Pixels("isolated-group-opacity-applied-once", [G(S(0,0,255),S(0,255,0)) with { Opacity = 128 },S(255,0,0)], [127,0,128,255]);
    Pixels("nested-isolated-groups", [G(G(S(0,0,255)),S(0,255,0)),S(255,0,0)], [0,0,255,255]);
    Pixels("pass-through-sees-external-backdrop", [G(S(255,255,0) with { Blend = "mul " }) with { Blend="pass" },S(0,0,255)], [0,0,0,255]);
    Pixels("normal-group-isolates-child-blend", [G(S(255,255,0) with { Blend = "mul " }),S(0,0,255)], [255,255,0,255]);
    Pixels("group-blend-applied-at-boundary", [G(S(255,255,0)) with { Blend="mul " },S(0,0,255)], [0,0,0,255]);
    Pixels("hidden-parent-hides-descendants", [G(S(0,0,255)) with { Visible=false },S(255,0,0)], [255,0,0,255]);
    Pixels("empty-group-is-transparent", [G(),S(255,0,0)], [255,0,0,255]);
    Pixels("mask-half-alpha", [S(0,0,255) with { Mask = M([128]) }], [0,0,128,128]);
    Pixels("mask-black-outside", [Wide(0,0,255,3) with { Mask = M([255],x:1) }], [0,0,0,0, 0,0,255,255, 0,0,0,0],3);
    Pixels("mask-white-outside", [Wide(0,0,255,3) with { Mask = M([0],x:1,outside:255) }], [0,0,255,255, 0,0,0,0, 0,0,255,255],3);
    Pixels("mask-inversion-includes-outside", [Wide(0,0,255,3) with { Mask = M([255],x:1,flags:4) }], [0,0,255,255, 0,0,0,0, 0,0,255,255],3);
    Pixels("mask-relative-layer-offset", [Wide(0,0,255,2) with { Bounds=new(1,0,2,1),Mask=M([0],outside:255,flags:1) }], [0,0,0,0, 0,0,0,0, 0,0,255,255],3);
    Pixels("mask-disabled", [S(0,0,255) with { Mask=M([0],flags:2) }], [0,0,255,255]);
    Pixels("group-mask-applied-after-composition", [G(S(0,0,255),S(0,255,0)) with { Mask=M([128]) },S(255,0,0)], [127,0,128,255]);
    Pixels("clip-preserves-base-alpha-and-background", [Wide(0,0,255,3) with { Clipping=true },
        new(Pixels:[255,0,0,255, 255,0,0,0, 255,0,0,128],Bounds:new(0,0,3,1)),Wide(0,255,0,3)],
        [0,0,255,255, 0,255,0,255, 0,127,128,255],3);
    Pixels("clip-base-opacity-applied-once", [S(0,0,255) with { Clipping=true },S(255,0,0) with { Opacity=128 },S(0,255,0)], [0,127,128,255]);
    Pixels("clip-chain-does-not-expand-coverage", [S(0,0,255) with { Clipping=true },S(0,255,0) with { Clipping=true },S(255,0,0,128)], [0,0,128,128]);
    Pixels("hidden-clipping-base-does-not-rebind", [S(0,0,255) with { Clipping=true },S(255,0,0) with { Visible=false },S(0,255,0)], [0,255,0,255]);
    Pixels("clip-inside-group-keeps-local-boundary", [G(S(0,0,255) with { Clipping=true },S(255,0,0,128)),S(0,255,0)], [0,127,128,255]);
    foreach (var (mode, expected) in new (string, byte[])[] {
        ("mul ",[0,0,0,255]), ("scrn",[255,0,255,255]), ("dark",[0,0,0,255]),
        ("lite",[255,0,255,255]), ("diff",[255,0,255,255]), ("smud",[255,0,255,255]),
        ("over",[0,0,255,255]), ("hLit",[255,0,0,255]) })
        Pixels("blend-"+mode.Trim(), [S(255,0,0) with { Blend=mode },S(0,0,255)], expected);

    Case("selection-prunes-hidden-subtree-and-reuses-output", () =>
    {
        var dir=Save([G(S(0,0,255),S(0,255,0)),S(255,0,0)]);
        using var pool=new SharedDocumentPool(4096,2); using var source=pool.Acquire(dir);
        using var r=new TreeCompiledRenderer(context,source);
        Check(r.Update([3])); var pointer=r.Output!.NativePointer; var reads=pool.Snapshot().BlockReadCount;
        Check(!r.Update([1,2,3])); Check(r.Output.NativePointer==pointer); Check(pool.Snapshot().BlockReadCount==reads);
        Check(r.Update([0,1,3])); Bytes([0,0,255,255],r.Readback());
        Check(!r.Update([3,1,0])); Check(r.Update([0,2,3])); Bytes([0,255,0,255],r.Readback());
    });
    Case("repeated-state-switches-release-graph", () =>
    {
        var dir=Save([G(S(0,0,255),S(0,255,0)),S(255,0,0)]);
        using var pool=new SharedDocumentPool(4096,2); using var source=pool.Acquire(dir);
        using var r=new TreeCompiledRenderer(context,source);
        for(var i=0;i<80;i++) { r.Update(i%2==0 ? [0,1,3] : [0,2,3]); Check(r.LiveGraphObjects==0 && r.LiveTemporaryBitmaps==0); }
        Check(r.CompositionCount==80); Check(pool.Snapshot().BlockReadCount==3);
        var old=r.Output!; r.Clear(); Check(old.NativePointer==IntPtr.Zero && r.Output is null);
        r.Update(); r.Dispose(); r.Dispose(); Throws<ObjectDisposedException>(()=>r.Update());
    });
    Case("upload-and-graph-budget-fail-cleanly", () =>
    {
        var dir=Save([G(S(0,0,255),S(0,255,0))]);
        using var pool=new SharedDocumentPool(4096,2); using var source=pool.Acquire(dir);
        using(var r=new TreeCompiledRenderer(context,source,maxUploadBytes:1))
        { Throws<CacheCapacityException>(()=>r.Update()); Check(r.Output is null && r.LiveGraphObjects==0); Check(pool.Snapshot().BlockReadCount==0); }
        using(var r=new TreeCompiledRenderer(context,source,maxGraphObjects:1))
        { Throws<CacheCapacityException>(()=>r.Update()); Check(r.Output is null && r.LiveGraphObjects==0 && r.LiveTemporaryBitmaps==0); }
    });
    Case("corruption-preserves-output-and-context", () =>
    {
        var dir=Save([S(0,0,255),S(255,0,0)]); long offset;
        using(var doc=new CompiledDocument(dir)) offset=doc.Manifest.Blocks[0].Offset;
        var bin=Path.Combine(dir,"pixels.bin"); var bytes=File.ReadAllBytes(bin); bytes[(int)offset]^=1; File.WriteAllBytes(bin,bytes);
        using var pool=new SharedDocumentPool(4096,2); using var source=pool.Acquire(dir);
        using var r=new TreeCompiledRenderer(context,source); r.Update([1]); var pointer=r.Output!.NativePointer;
        var transform=Matrix3x2.CreateTranslation(3,4); context.Transform=transform;
        try { Throws<InvalidDataException>(()=>r.Update()); Check(r.Output.NativePointer==pointer && r.CompositionCount==1);
            Check(r.LiveGraphObjects==0 && context.Transform==transform); Bytes([255,0,0,255],r.Readback()); }
        finally { context.Transform=Matrix3x2.Identity; }
    });
    Case("context-target-dpi-and-blend-restored", () =>
    {
        using var target=context.CreateBitmap(new SizeI(1,1),new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,Vortice.DCommon.AlphaMode.Premultiplied),96,96,BitmapOptions.Target));
        context.Target=target; context.Dpi=new(120,144); context.UnitMode=UnitMode.Dips;
        context.Transform=Matrix3x2.CreateTranslation(7,9); context.PrimitiveBlend=PrimitiveBlend.Copy;
        try {
            var dir=Save([G(S(0,0,255))]); using var pool=new SharedDocumentPool(4096,2); using var source=pool.Acquire(dir);
            using var r=new TreeCompiledRenderer(context,source); r.Update();
            using var actual=context.Target; Check(actual!.NativePointer==target.NativePointer);
            Check(context.Dpi.Width==120 && context.Dpi.Height==144 && context.UnitMode==UnitMode.Dips);
            Check(context.Transform==Matrix3x2.CreateTranslation(7,9) && context.PrimitiveBlend==PrimitiveBlend.Copy);
        } finally { context.Target=null; context.Dpi=new(96,96); context.UnitMode=UnitMode.Pixels;
            context.Transform=Matrix3x2.Identity; context.PrimitiveBlend=PrimitiveBlend.SourceOver; }
    });
    Reject("unsupported-blend",[S(0,0,255) with { Blend="xxxx" }]);
    Reject("translucent-pass-through",[G(S(0,0,255)) with { Blend="pass",Opacity=128 }]);
    Reject("masked-pass-through",[G(S(0,0,255)) with { Blend="pass",Mask=M([255]) }]);
    Reject("non-normal-clip",[S(0,0,255) with { Blend="mul ",Clipping=true },S(255,0,0)]);
    Reject("orphan-clipping",[S(0,0,255) with { Clipping=true }]);
    Reject("pass-through-clipping-base",[S(0,0,255) with { Clipping=true },G(S(255,0,0)) with { Blend="pass" }]);
    Reject("mask-feather",[S(0,0,255) with { Mask=M([128]) with { Feather=1 } }]);
    Reject("dissolve-needs-separate-proof",[S(0,0,255,128) with { Blend="diss" }]);

    void Pixels(string name, Spec[] specs, byte[] expected, int width=1) => Case(name,()=>
    {
        var dir=Save(specs,width); using var pool=new SharedDocumentPool(4096,2); using var source=pool.Acquire(dir);
        using var r=new TreeCompiledRenderer(context,source); Check(r.Update()); Bytes(expected,r.Readback());
        Check(r.LiveGraphObjects==0 && r.LiveTemporaryBitmaps==0); Check(!r.Update()); Check(r.CompositionCount==1);
    });
    void Reject(string name, Spec[] specs) => Case(name+"-rejected-before-read",()=>
    {
        var dir=Save(specs); using var pool=new SharedDocumentPool(4096,2); using var source=pool.Acquire(dir);
        using var r=new TreeCompiledRenderer(context,source); Throws<NotSupportedException>(()=>r.Update());
        Check(r.Output is null && r.LiveGraphObjects==0 && pool.Snapshot().BlockReadCount==0);
    });
}
catch(Exception ex) { failed++; results.Add(new { name="device-or-harness",status="FAIL",error=ex.ToString() }); Console.WriteLine(ex); }
finally { Directory.Delete(root,true); }
var output=args.Length==1 ? args[0] : "tree-renderer-results.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output,JsonSerializer.Serialize(new { schema="psd-next.tree-renderer-proof.v1",total=results.Count,failed,assertions,
    tolerance=0,profile=TreeCompiledRenderer.Profile,backend="D3D11 WARP / Direct2D",realYmm4Executed=false,
    physicalGpuPerformanceClaim=false,sourceHead=Environment.GetEnvironmentVariable("SOURCE_HEAD"),results },new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"TREE RENDERER SUMMARY: {results.Count-failed}/{results.Count} cases passed; {assertions} assertions; {failed} failures; pixel tolerance=0.");
return failed==0 ? 0 : 1;

void Case(string name,Action test) { try { test();results.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name); }
    catch(Exception ex){failed++;results.Add(new{name,status="FAIL",error=ex.ToString()});Console.WriteLine("FAIL "+name+": "+ex);} }
void Check(bool value){assertions++;if(!value)throw new Exception("Assertion failed.");}
void Bytes(byte[] expected,byte[] actual){assertions++;if(!expected.AsSpan().SequenceEqual(actual))
    throw new Exception($"Expected {Convert.ToHexString(expected)}; got {Convert.ToHexString(actual)}.");}
void Throws<T>(Action action) where T:Exception {assertions++;try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
Spec S(byte b,byte g,byte r,byte a=255)=>new(Pixels:[b,g,r,a]);
Spec Wide(byte b,byte g,byte r,int width)=>new(Pixels:Enumerable.Range(0,width).SelectMany(_=>new byte[]{b,g,r,255}).ToArray(),Bounds:new(0,0,width,1));
Spec G(params Spec[] children)=>new(Children:children);
MaskSpec M(byte[] pixels,int x=0,int y=0,byte outside=0,int flags=0)=>new(pixels,new(x,y,pixels.Length,1),outside,flags);
string Save(Spec[] specs,int width=1,int height=1)
{
    var dir=Path.Combine(root,Guid.NewGuid().ToString("N")); using var writer=new CompiledStoreWriter(dir);
    var nodes=new List<LayerNode>(); Add(specs,null);
    var identity=new SourceFingerprint(CompiledFormat.Hash(new byte[26]),26);
    writer.Complete(dir,new CompiledManifest(1,CompiledFormat.CompilerId,CompiledFormat.Generation(identity),identity,
        1,width,height,8,3,null,null,nodes.ToImmutableArray(),writer.Blocks)); return dir;
    void Add(Spec[] siblings,int? parent)
    {
        for(var order=0;order<siblings.Length;order++)
        {
            var s=siblings[order];var id=nodes.Count;var group=s.Children is not null;
            var bounds=s.Bounds??(group?new PixelRect(0,0,0,0):new PixelRect(0,0,1,1));
            int? block=group?null:writer.AddBlock(BlockFormat.Bgra8Straight,bounds.Width,bounds.Height,s.Pixels!,compress:false);
            MaskInfo? mask=null;
            if(s.Mask is {} m){var mb=writer.AddBlock(BlockFormat.Gray8,m.Bounds.Width,m.Bounds.Height,m.Pixels,compress:false);
                mask=new(m.Bounds,m.DefaultColor,m.Flags,0,0,m.Feather,0,0,new(0,0,0,0),[new MaskPlane(-2,mb)]);}
            nodes.Add(new(id,parent,order,group?NodeKind.Group:NodeKind.Layer,s.Name??("node"+id),bounds,
                s.Visible?(byte)0:(byte)2,s.Visible,s.Blend,s.Opacity,s.Clipping,block,mask,[]));
            if(group)Add(s.Children!,id);
        }
    }
}
sealed record Spec(byte[]? Pixels=null,PixelRect? Bounds=null,Spec[]? Children=null,string Blend="norm",byte Opacity=255,bool Clipping=false,MaskSpec? Mask=null,bool Visible=true,string? Name=null);
sealed record MaskSpec(byte[] Pixels,PixelRect Bounds,byte DefaultColor,int Flags,double Feather=0);
