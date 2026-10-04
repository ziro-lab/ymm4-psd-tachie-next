using System.Runtime.Loader;
using System.IO;
using Newtonsoft.Json;
using PsdTachieNext.Core;
using PsdTachieNext.Ymm4;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.UndoRedo;

var host = Path.GetFullPath(Environment.GetEnvironmentVariable("YMM4_COMPONENT_HOST") ?? throw new InvalidOperationException("Set YMM4_COMPONENT_HOST to the authorized validation host."));
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var file = Path.Combine(host, name.Name + ".dll");
    return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
};
await Probe.Run(args[0]);

internal static class Probe
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static async Task Run(string output)
    {
        var results = new List<object>();
        foreach (var mode in new[] { "face-replace", "face-edit-replace", "face-inplace", "standing-replace" })
        {
            try
            {
                var before = PsdAppearanceSettings.Create("before"); var after = PsdAppearanceSettings.Create("after");
                var oldFace = new CompiledFaceParameter { Appearance = before };
                var oldStanding = new CompiledItemParameter { Appearance = before };
                var face = new TachieFaceItem { TachieFaceParameter = oldFace };
                var standing = new TachieItem { TachieItemParameter = oldStanding };
                var manager = new UndoRedoManager(); var commands = 0;
                if (mode == "standing-replace") { manager.Subscribe(standing); standing.UndoRedoCommandCreated += (_, _) => commands++; }
                else { manager.Subscribe(face); face.UndoRedoCommandCreated += (_, _) => commands++; }
                if (mode == "face-edit-replace") face.BeginEdit();
                if (mode == "standing-replace") standing.TachieItemParameter = new CompiledItemParameter { Appearance = after };
                else if (mode == "face-inplace") oldFace.Appearance = after;
                else face.TachieFaceParameter = new CompiledFaceParameter { Appearance = after };
                if (mode == "face-edit-replace") await face.EndEditAsync();
                manager.Record(); var undoable = manager.IsUndoable; var beforeUndoCommands = commands;
                await manager.UndoAsync();
                var restored = mode == "standing-replace" ? ((CompiledItemParameter)standing.TachieItemParameter).Appearance
                    : ((CompiledFaceParameter)face.TachieFaceParameter).Appearance;
                results.Add(new { mode, undoable, beforeUndoCommands, restoredBefore = restored == before, current = restored?.Json });
            }
            catch (Exception ex) { results.Add(new { mode, error = ex.ToString() }); }
        }
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        foreach (var refreshClones in new[] { false, true })
        {
            var before = PsdAppearanceSettings.Create("base"); var hairOff = PsdAppearanceSettings.Create("hair-off"); var eyeOn = PsdAppearanceSettings.Create("eye-on");
            var standing = new TachieItem { TachieItemParameter = new CompiledItemParameter { Appearance = before } };
            var face = new TachieFaceItem { TachieFaceParameter = new CompiledFaceParameter() };
            var manager = new UndoRedoManager(); manager.Subscribe(standing); manager.Subscribe(face);
            standing.TachieItemParameter = new CompiledItemParameter { Appearance = hairOff }; manager.Record();
            if (refreshClones) Refresh();
            await manager.UndoAsync(); if (refreshClones) Refresh();
            await manager.RedoAsync(); if (refreshClones) Refresh();
            await manager.UndoAsync(); if (refreshClones) Refresh();
            var beforeFace = ((CompiledItemParameter)standing.TachieItemParameter).Appearance;
            face.TachieFaceParameter = new CompiledFaceParameter { Appearance = eyeOn }; manager.Record();
            if (refreshClones) Refresh();
            await manager.UndoAsync();
            results.Add(new { mode = "standing-history-branch-then-face", refreshClones,
                beforeFace = beforeFace?.Json, standingAfterFaceUndo = ((CompiledItemParameter)standing.TachieItemParameter).Appearance?.Json,
                faceAfterUndo = ((CompiledFaceParameter)face.TachieFaceParameter).Appearance?.Json,
                unrelatedStandingPreserved = ((CompiledItemParameter)standing.TachieItemParameter).Appearance == before });
            void Refresh() => standing.TachieItemParameter = ((CompiledItemParameter)standing.TachieItemParameter).CreateEquivalentRefreshClone();
        }
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var notificationParameter = new RefreshNotificationParameter { State = "base" };
        var notificationItem = new TachieItem { TachieItemParameter = notificationParameter };
        var notificationFace = new TachieFaceItem { TachieFaceParameter = new CompiledFaceParameter() };
        var notificationManager = new UndoRedoManager(); notificationManager.Subscribe(notificationItem); notificationManager.Subscribe(notificationFace);
        var notices = 0; notificationItem.UndoRedoCommandCreated += (_, e) => { if(e.Command.IsEmpty) notices++; };
        notificationParameter.Notify();
        var emptyOnlyUndoable = notificationManager.IsUndoable; notificationManager.Record();
        var emptyRecordUndoable = notificationManager.IsUndoable;
        notificationParameter.State = "hair-off"; notificationManager.Record(); notificationParameter.Notify();
        await notificationManager.UndoAsync(); notificationParameter.Notify();
        var redoPreserved = notificationManager.IsRedoable;
        await notificationManager.RedoAsync(); notificationParameter.Notify();
        await notificationManager.UndoAsync(); notificationParameter.Notify();
        notificationFace.TachieFaceParameter = new CompiledFaceParameter { Appearance = PsdAppearanceSettings.Create("eye-on") }; notificationManager.Record();
        notificationParameter.Notify(); await notificationManager.UndoAsync();
        results.Add(new {mode="empty-native-property-notification", notices, emptyOnlyUndoable, emptyRecordUndoable, redoPreserved,
            unrelatedStandingPreserved=notificationParameter.State=="base", faceRestored=((CompiledFaceParameter)notificationFace.TachieFaceParameter).Appearance is null});
        foreach(var sendNotice in new[]{false,true})
        {
            var readyParameter=new RefreshNotificationParameter{State="base"};
            var readyItem=new TachieItem{TachieItemParameter=readyParameter};
            var userItem=new TachieFaceItem{TachieFaceParameter=new CompiledFaceParameter()};
            var pendingManager=new UndoRedoManager();pendingManager.Subscribe(readyItem);pendingManager.Subscribe(userItem);
            readyParameter.State="hair-off";pendingManager.Record();await pendingManager.UndoAsync();
            var beforePending=new{pendingManager.IsUndoable,pendingManager.IsRedoable};
            var userBefore=JsonConvert.SerializeObject(userItem);
            userItem.TachieFaceParameter=new CompiledFaceParameter{Appearance=PsdAppearanceSettings.Create("pending-user")};
            var pendingJson=JsonConvert.SerializeObject(userItem);
            var beforeNotice=new{pendingManager.IsUndoable,pendingManager.IsRedoable};
            if(sendNotice)readyParameter.Notify();
            var afterNotice=new{pendingManager.IsUndoable,pendingManager.IsRedoable};
            var pendingPreserved=JsonConvert.SerializeObject(userItem)==pendingJson;
            pendingManager.Record();await pendingManager.UndoAsync();
            var oneUndoRestoresUser=JsonConvert.SerializeObject(userItem)==userBefore && readyParameter.State=="base";
            await pendingManager.RedoAsync();
            var oneRedoRestoresUser=JsonConvert.SerializeObject(userItem)==pendingJson && readyParameter.State=="base";
            results.Add(new{mode="unrecorded-unrelated-user-edit",sendNotice,beforePending,beforeNotice,afterNotice,
                pendingPreserved,oneUndoRestoresUser,oneRedoRestoresUser,
                nativeMainIsSaved="Not instantiated by SDK-only manager probe; public MainViewModel.IsSaved exists and needs native observation"});
        }
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
internal sealed class RefreshNotificationParameter : YukkuriMovieMaker.Plugin.Tachie.TachieItemParameterBase
{
    private string state="";
    public string State {get=>state;set=>Set(ref state,value,nameof(State));}
    public void Notify()
    {
        var command=new UndoRedoPropertyChangedCommand<RefreshNotificationParameter,string>(this,nameof(State),state,state);
        if(!command.IsEmpty) throw new InvalidOperationException("SDK same-value command must be empty");
        InvokeUndoRedoCommandCreatedEvent(new UndoRedoEventArgs(command));
    }
    protected override IEnumerable<YukkuriMovieMaker.Commons.IAnimatable> GetAnimatables()=>[];
}

