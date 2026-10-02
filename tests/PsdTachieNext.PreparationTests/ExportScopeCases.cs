using PsdTachieNext.Core;
using PsdTachieNext.Compiler;
using PsdTachieNext.Tests;

static class ExportScopeCases
{
    internal static async Task Run(string root,Func<string,Func<Task>,Task> test,Action<bool> check)
    {
        var source=SourceAssetRef.Create(Path.Combine(root,"export-scope.psd"));PsdFixture.Write(source.Path);
        var stamp=new RequestStamp(Guid.NewGuid(),1,3,4,5,6);
        ExportRequestScope Scope(){var scope=new ExportRequestScope(source,3,4);scope.Bind(stamp);return scope;}
        bool Reject(Action action){try{action();return false;}catch(IOException){return true;}}
        await test("export-scope-fixes-reference-stamp-all-revisions-and-latches-failure",()=>
        {
            var scope=Scope();scope.Validate(source,stamp,3,4);
            foreach(var changed in new[]{stamp with{AssetRequestId=2},stamp with{SessionId=Guid.NewGuid()},
                stamp with{SourceRevision=4},stamp with{SettingsRevision=5},stamp with{AppearanceRevision=6},stamp with{DeviceEpoch=7}})
            {var independent=Scope();check(Reject(()=>independent.Validate(source,changed,3,4)));check(Reject(()=>independent.Validate(source,stamp,3,4)));}
            var clear=Scope();check(Reject(()=>clear.Validate(null,stamp,3,4)));check(Reject(()=>clear.Validate(source,stamp,3,4)));
            var relink=Scope();check(Reject(()=>relink.Validate(source.Relink(source.Path+".other"),stamp,3,4)));
            var logical=Scope();check(Reject(()=>logical.Validate(SourceAssetRef.Create(source.Path),stamp,3,4)));
            var aba=Scope();check(Reject(()=>aba.Validate(source,stamp,3,6)));
            return Task.CompletedTask;
        });
        await test("export-scope-delayed-invalidation-stays-in-retired-episode",()=>
        {
            var retired=Scope();var next=Scope();retired.Invalidate(source.Path,4);
            check(Reject(()=>retired.Validate(source,stamp,3,4)));next.Validate(source,stamp,3,4);
            next.Invalidate(source.Path+".other",99);next.Invalidate(source.Path,3);next.Validate(source,stamp,3,4);
            check(true);return Task.CompletedTask;
        });
        await test("export-scope-notification-does-not-join-blocked-preparation-owner",async()=>
        {
            using var service=new SourcePreparationService(new CompiledAssetRepository(Path.Combine(root,"export-blocked-cache")));
            using var pool=new SharedDocumentPool(4096,4);using var session=new SourceSession();
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.BeforeSnapshot=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
            var frozen=new ExportRequestScope(source,0,0);var own=new object();
            var request=session.Request(0,0,0,async(_,token)=>{using var asset=await service.PrepareAsync(source,0,token);return asset.PrepareAppearance(pool,token:token);});
            frozen.Bind(request);var strict=Task.Run(()=>{lock(own)session.Completion.GetAwaiter().GetResult();});
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(()=>frozen.Invalidate(source.Path,1)).WaitAsync(TimeSpan.FromSeconds(1));
                check(!strict.IsCompleted);check(Reject(()=>frozen.Validate(source,request,0,0)));
                release.TrySetResult();await strict.WaitAsync(TimeSpan.FromSeconds(5));
                using var ready=session.TakeReady(out var completed);check(ready is not null&&completed==request);
                check(Reject(()=>frozen.Validate(source,completed,0,0))); // Ready CPU data cannot resurrect a failed export.
            }
            finally{release.TrySetResult();}
        });
    }
}
