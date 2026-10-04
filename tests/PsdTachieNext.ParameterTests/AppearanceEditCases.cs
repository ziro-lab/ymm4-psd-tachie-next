using System.ComponentModel;
using System.IO;
using System.Text.Json;
using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;
using PsdTachieNext.Ymm4;
using YukkuriMovieMaker.UndoRedo;

internal static class AppearanceEditCases
{
    internal static void Run(string output)
    {
        int total=0,failed=0,assertions=0;
        var cases=new List<object>();
        var directory=Path.Combine(Path.GetDirectoryName(output)!,"appearance-edit-fixture");
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"synthetic.psd");
        PsdFixture.Write(path,visibleName:"*base",hiddenName:"*other",layerIds:[11,12]);
        var reference=SourceAssetRef.Create(path);
        var compiled=new PsdCompiler().Compile(path,Path.Combine(directory,"compiled"));
        using var pool=new SharedDocumentPool(4096,1);
        using var document=pool.Acquire(compiled);
        var index=PsdLayerReferenceIndex.Read(document);
        var edit=PsdAppearanceSettings.Create(reference.AssetIdentity).SetVisible(reference.AssetIdentity,index,1,true)
            .Settings!.WithFlip(reference.AssetIdentity,index,PsdFlipState.XY);
        Check(edit.Succeeded,"synthetic edit resolves");
        CompiledItemParameter Baseline()=>new(){Source=reference};
        void Case(string name,Action action)
        {
            total++;
            try{action();cases.Add(new{name,passed=true});}
            catch(Exception ex){failed++;cases.Add(new{name,passed=false,error=ex.ToString()});}
        }
        void Check(bool condition,string message){assertions++;if(!condition)throw new InvalidOperationException(message);}
        AppearanceEditCommitException Failure(Action action)
        {
            try{action();}catch(AppearanceEditCommitException ex){return ex;}
            throw new InvalidOperationException("Expected explicit appearance commit uncertainty.");
        }

        Case("complete edit before Record",()=>
        {
            var before=Baseline();var current=before;int setters=0,records=0;
            var applied=AppearanceEditBridge.CommitCore(()=>current,next=>{setters++;current=next;},before,before.RefreshRevision,edit,()=>
            {
                records++;Check(current.Appearance==edit.Settings,"Record observes whole JSON");
                var resolved=current.Appearance!.Resolve(reference.AssetIdentity,index);
                Check(resolved.State!.FlipState==PsdFlipState.XY && resolved.State.EnabledNodeIds.SequenceEqual([1]),"Record observes mask and flip together");
            });
            Check(applied && setters==1 && records==1,"one successful setter/record");
            Check(before.Appearance is null && !ReferenceEquals(current,before),"original immutable parameter retained");
        });
        foreach(var duringSetter in new[]{true,false})Case("normal return after owner displacement: "+(duringSetter?"setter":"Record"),()=>
        {
            var before=Baseline();var current=before;var other=Baseline();CompiledItemParameter? replacement=null;
            int setters=0,records=0;var revision=before.RefreshRevision;
            var returned=AppearanceEditBridge.CommitCore(()=>current,next=>
            {setters++;replacement=next;current=next;if(duringSetter)current=other;},before,revision,edit,()=>
            {records++;if(!duringSetter)current=other;});
            Check(returned && setters==1 && records==1,"true means both calls returned, even after displacement");
            Check(ReferenceEquals(current,other) && current.Appearance is null,"caller re-read detects edited intent is not retained");
            Check(replacement!.Appearance==edit.Settings && before.Appearance is null,"prepared edit and immutable original remain distinct from actual owner");
            Check(!AppearanceEditBridge.CommitCore(()=>current,_=>setters++,before,revision,edit,()=>records++),"old-plan retry rejects the actual other parameter");
            Check(setters==1 && records==1 && ReferenceEquals(current,other),"no automatic rollback, retry or overwrite");
        });
        Case("stale owner rejects without mutation",()=>
        {
            var before=Baseline();var current=Baseline();int setters=0,records=0;
            Check(!AppearanceEditBridge.CommitCore(()=>current,_=>setters++,before,before.RefreshRevision,edit,()=>records++),"stale rejected");
            Check(setters==0 && records==0,"stale has no effect");
        });
        Case("ABA revision rejects without mutation",()=>
        {
            var before=Baseline();var revision=before.RefreshRevision;before.Appearance=edit.Settings;before.Appearance=null;
            int setters=0,records=0;
            Check(!AppearanceEditBridge.CommitCore(()=>before,_=>setters++,before,revision,edit,()=>records++),"ABA rejected");
            Check(setters==0 && records==0,"ABA has no effect");
        });
        Case("unresolved edit rejects without mutation",()=>
        {
            var before=Baseline();int setters=0,records=0;
            var invalid=PsdAppearanceSettings.Create("foreign-asset").Resolve(reference.AssetIdentity,index);
            Check(!invalid.Succeeded,"invalid fixture fails");
            Check(!AppearanceEditBridge.CommitCore(()=>before,_=>setters++,before,before.RefreshRevision,invalid,()=>records++),"invalid edit rejected");
            Check(setters==0 && records==0,"invalid has no effect");
        });
        Case("same appearance is not a history operation",()=>
        {
            var before=Baseline();before.Appearance=edit.Settings;int setters=0,records=0;
            Check(!AppearanceEditBridge.CommitCore(()=>before,_=>setters++,before,before.RefreshRevision,edit,()=>records++),"equal rejected");
            Check(setters==0 && records==0,"equal has no effect");
        });
        foreach(var partial in new[]{false,true})Case(partial?"partial Record failure retains history effects":"early Record failure retains edit",()=>
        {
            var before=Baseline();var current=before;int setters=0,records=0,historyEffects=0;
            var cause=new InvalidOperationException("Injected Record failure");
            var revision=before.RefreshRevision;
            var error=Failure(()=>AppearanceEditBridge.CommitCore(()=>current,next=>{setters++;current=next;},before,revision,edit,()=>
            {records++;if(partial)historyEffects++;throw cause;}));
            Check(error.Stage==AppearanceEditFailureStage.HistoryRecord,"Record stage reported");
            Check(error.ParameterObservation==AppearanceEditParameterObservation.Replacement && error.ObservedAppearance==edit.Settings,"retained edit reported");
            Check(ReferenceEquals(error.InnerException,cause) && error.HistoryMayHaveChanged,"original cause/unknown history retained");
            Check(current.Appearance==edit.Settings && before.Appearance is null,"no rollback");
            Check(setters==1 && records==1 && historyEffects==(partial?1:0),"partial effects are not undone");
            Check(!AppearanceEditBridge.CommitCore(()=>current,_=>setters++,before,revision,edit,()=>records++),"old-plan retry rejects");
            Check(setters==1 && records==1,"no blind duplicate Record");
        });
        foreach(var mutate in new[]{false,true})Case(mutate?"setter throws after mutation":"setter throws before mutation",()=>
        {
            var before=Baseline();var current=before;int setters=0,records=0;
            var cause=new InvalidOperationException("Injected setter notification failure");
            var error=Failure(()=>AppearanceEditBridge.CommitCore(()=>current,next=>{setters++;if(mutate)current=next;throw cause;},
                before,before.RefreshRevision,edit,()=>records++));
            Check(error.Stage==AppearanceEditFailureStage.ParameterReplacement,"setter stage reported");
            Check(error.ParameterObservation==(mutate?AppearanceEditParameterObservation.Replacement:AppearanceEditParameterObservation.Original),"setter actual state observed");
            Check(error.HistoryMayHaveChanged && ReferenceEquals(error.InnerException,cause),"no false clean-history claim");
            Check(setters==1 && records==0,"no Record after failed setter, no rollback setter");
        });
        Case("callback changes owner before failure",()=>
        {
            var before=Baseline();var current=before;var other=Baseline();int setters=0;
            var error=Failure(()=>AppearanceEditBridge.CommitCore(()=>current,next=>{setters++;current=next;},before,before.RefreshRevision,edit,()=>
            {current=other;throw new InvalidOperationException("Owner changed");}));
            Check(error.ParameterObservation==AppearanceEditParameterObservation.Other,"other owner reported");
            Check(ReferenceEquals(current,other) && setters==1,"other owner not overwritten by rollback");
        });
        Case("failed diagnostic read preserves original Record exception",()=>
        {
            var before=Baseline();var current=before;int reads=0;
            var cause=new InvalidOperationException("Record cause");
            var error=Failure(()=>AppearanceEditBridge.CommitCore(()=>++reads==1?current:throw new InvalidOperationException("Diagnostic read failed"),
                next=>current=next,before,before.RefreshRevision,edit,()=>throw cause));
            Check(error.ParameterObservation==AppearanceEditParameterObservation.Unavailable,"unavailable explicitly reported");
            Check(ReferenceEquals(error.InnerException,cause) && current.Appearance==edit.Settings,"diagnostic failure cannot mask cause or rollback");
        });

        foreach(var faultEvent in new[]{"Recorded","HistoryChanged","IsUndoable"})Case("real public manager partial failure: "+faultEvent,()=>
        {
            var before=Baseline();var current=before;int setters=0,undo=0,redo=0,faults=0;
            var manager=new UndoRedoManager();
            var cause=new InvalidOperationException("Injected actual host manager subscriber failure");
            EventHandler fault=(_,_)=>{faults++;throw cause;};
            PropertyChangedEventHandler propertyFault=(_,e)=>{if(e.PropertyName=="IsUndoable"){faults++;throw cause;}};
            if(faultEvent=="Recorded")manager.Recorded+=fault;
            if(faultEvent=="HistoryChanged")manager.HistoryChanged+=fault;
            if(faultEvent=="IsUndoable")manager.PropertyChanged+=propertyFault;
            try
            {
                var error=Failure(()=>AppearanceEditBridge.CommitCore(()=>current,next=>
                {
                    setters++;current=next;
                    manager.AddCommand(new UndoRedoActionCommand(()=>{undo++;current=before;},()=>{redo++;current=next;}));
                },before,before.RefreshRevision,edit,manager.Record));
                Check(error.Stage==AppearanceEditFailureStage.HistoryRecord && error.HistoryMayHaveChanged,"actual Record uncertainty reported");
                Check(error.ParameterObservation==AppearanceEditParameterObservation.Replacement && ReferenceEquals(error.InnerException,cause),"actual error/state retained");
                Check(faults==1 && manager.IsUndoable && current.Appearance==edit.Settings && setters==1,"Record threw after actual history committed; no rollback");
                manager.Recorded-=fault;manager.HistoryChanged-=fault;manager.PropertyChanged-=propertyFault;
                manager.UndoAsync().GetAwaiter().GetResult();
                Check(undo==1 && ReferenceEquals(current,before) && manager.IsRedoable,"one public Undo restores original value");
                manager.RedoAsync().GetAwaiter().GetResult();
                Check(redo==1 && current.Appearance==edit.Settings && setters==1,"one public Redo restores exact edit");
            }
            finally{manager.Recorded-=fault;manager.HistoryChanged-=fault;manager.PropertyChanged-=propertyFault;manager.Clear();}
        });
        File.WriteAllText(output,JsonSerializer.Serialize(new{total,assertions,failed,cases,
            scope="managed bridge operation and exact-host public manager with synthetic action commands; not live Timeline subscription",
            guiLaunched=false,nativeTimelineUndo=false,nativeSave=false},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"APPEARANCE EDIT FAILURE SUMMARY: {total-failed}/{total}; {assertions} assertions; {failed} failures.");
        if(failed!=0)throw new InvalidOperationException("Appearance edit failure cases failed; preserved results.");
    }
}
