using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PsdTachieNext.Core;
using PsdTachieNext.Compiler;
using PsdTachieNext.Tests;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.UndoRedo;
using JsonConvert = Newtonsoft.Json.JsonConvert;
using PsdTachieNext.Direct2D;
using PsdTachieNext.Ymm4;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Tachie;

namespace PsdTachieNext.HostProof;

/// <summary>Test-only startup callback in a temporary real YMM4 process; not a timeline-UI test.</summary>
public sealed class ProofPlugin : ILocalizePlugin
{
    private static int started;
    public string Name => "PSD Tachie Next private integration proof";
    public void SetCulture(CultureInfo cultureInfo)
    {
        if (SparseNativeProof.Schedule()) return;
        if (LiveRefreshProof.Schedule()) return;
        var output = Environment.GetEnvironmentVariable("PSD_NEXT_HOST_PROOF_OUTPUT");
        if (string.IsNullOrWhiteSpace(output) || Interlocked.Exchange(ref started, 1) != 0) return;
        Directory.CreateDirectory(output);
        _ = Task.Run(() => Run(output));
    }
    private static void Run(string output)
    {
        var results = new List<object>(); var failures = 0; var assertions = 0;
        var observations = new List<object>();
        var temp = Path.Combine(Path.GetTempPath(), "psd-next-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, out ID3D11Device? createdDevice).CheckError();
            using var d3d = createdDevice ?? throw new InvalidOperationException("D3D11 returned no device.");
            using var dxgi = d3d.QueryInterface<IDXGIDevice>();
            using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
            using var device = factory.CreateDevice(dxgi);
            using var context = device.CreateDeviceContext(DeviceContextOptions.None);
            var first = Store(temp, 0); var second = Store(temp, 1);
            byte[] rawRed = [0,0,255,255, 0,0,255,255];
            byte[] rawBlue = [255,0,0,255, 255,0,0,255];
            byte[] rawGreen = [0,255,0,255, 0,255,0,255];
            // These references bypass the compiler, pool and product renderer completely.
            // A 1px-tall image centers at y=-0.5. Inverting the context transform does NOT
            // promise to undo the effect's interpolation. Compare the same declared pipeline.
            var red = ReferenceCentered(rawRed, 2, 1);
            var blue = ReferenceCentered(rawBlue, 2, 1);
            var green = ReferenceCentered(rawGreen, 2, 1);
            observations.Add(new { name="odd-centering-control", raw=Convert.ToHexString(rawRed),
                centeredReference=Convert.ToHexString(red), interpolation="Direct2D default linear / soft border" });
            var plugin = new CompiledTachiePlugin();
            var cp = plugin.CreateCharacterParameter(); var ip = (CompiledItemParameter)plugin.CreateItemParameter();
            var fp = plugin.CreateFaceParameter();
            using var pool = new SharedDocumentPool(4096, 4);
            Case("public-plugin-and-parameter-contracts", () =>
            {
                Check(cp is CompiledCharacterParameter && fp is CompiledFaceParameter); Check(!plugin.HasScriptFile);
                Check(typeof(ITachieSource).IsAssignableFrom(typeof(CompiledTachieSource)));
            });
            Case("host-bound-renderer-retains-exact-odd-sized-raw-pixels", () =>
            {
                using var source = pool.Acquire(Path.GetDirectoryName(first)!);
                using var renderer = new TreeCompiledRenderer(context, source);
                renderer.Update([0]); Bytes(rawRed, renderer.Readback());
                renderer.Update([1]); Bytes(rawBlue, renderer.Readback());
                Check(renderer.LiveGraphObjects == 0 && renderer.LiveTemporaryBitmaps == 0);
            });
            Case("odd-centering-reference-is-not-raw-pixel-identity", () =>
            {
                Check(!red.AsSpan().SequenceEqual(rawRed));
                using var source = new CompiledTachieSource(context, pool); source.UpdateCompiled(first);
                Bytes(red, Read(source.Output));
            });
            Case("diagnostic-source-update-renders-centered-stable-output", () =>
            {
                using var source = new CompiledTachieSource(context, pool); var pointer = source.Output.NativePointer;
                source.UpdateCompiled(first);
                Bytes(red, Read(source.Output)); Check(pointer == source.Output.NativePointer);
                var reads = pool.Snapshot().BlockReadCount;
                source.UpdateCompiled(first);
                Check(source.CompositionCount == 1 && pool.Snapshot().BlockReadCount == reads);
            });
            Case("selection-update-reuses-output-wrapper", () =>
            {
                using var source = new CompiledTachieSource(context, pool); source.UpdateCompiled(first, [0]);
                var pointer = source.Output.NativePointer;
                source.UpdateCompiled(first, [1]); Bytes(blue, Read(source.Output));
                Check(pointer == source.Output.NativePointer && source.CompositionCount == 2);
                Check(!source.UpdateCompiled(first, [1]));
            });
            Case("multiple-sources-share-document-not-expression", () =>
            {
                using var a = new CompiledTachieSource(context, pool); using var b = new CompiledTachieSource(context, pool);
                a.UpdateCompiled(first, [0]); b.UpdateCompiled(first, [1]);
                Check(pool.Snapshot().ActiveDocuments == 1 && pool.Snapshot().BlockReadCount == 2);
                Bytes(red, Read(a.Output)); Bytes(blue, Read(b.Output));
                a.Clear(); Check(pool.Snapshot().ActiveDocuments == 1);
                b.Clear(); Check(pool.Snapshot().ActiveDocuments == 0);
            });
            Case("dedicated-context-does-not-close-caller-draw-batch", () =>
            {
                using var target = Target(2, 1); using var source = new CompiledTachieSource(context, pool);
                context.Target = target; context.Transform = Matrix3x2.CreateTranslation(4, 5); context.BeginDraw();
                try
                {
                    context.Clear(new Color4(0, 0, 0, 0));
                    source.UpdateCompiled(first);
                    using var actual = context.Target; Check(actual!.NativePointer == target.NativePointer);
                    Check(context.Transform == Matrix3x2.CreateTranslation(4, 5));
                    context.Transform = Matrix3x2.CreateTranslation(1, .5f); context.DrawImage(source.Output);
                }
                finally { context.EndDraw().CheckError(); context.Target = null; context.Transform = Matrix3x2.Identity; }
                Bytes(red, ReadBitmap(target));
            });
            Case("bad-replacement-does-not-publish-old-generation-as-new", () =>
            {
                using var source = new CompiledTachieSource(context, pool); source.UpdateCompiled(first);
                var gen = source.CurrentGeneration;
                Throws<ArgumentException>(() => source.UpdateCompiled(Path.Combine(temp, "source.psd")));
                Throws<IOException>(() => source.UpdateCompiled(Path.Combine(temp, "missing", "manifest.json")));
                Check(gen == source.CurrentGeneration && pool.Snapshot().ActiveDocuments == 1);
                Bytes(red, Read(source.Output));
            });
            Case("manifest-generation-switch-retires-old-lease", () =>
            {
                using var source = new CompiledTachieSource(context, pool); source.UpdateCompiled(first);
                var gen = source.CurrentGeneration;
                source.UpdateCompiled(second); Check(source.CurrentGeneration != gen && pool.Snapshot().ActiveDocuments == 1);
                Bytes(green, Read(source.Output));
            });
            Case("empty-input-clears-and-final-dispose-releases", () =>
            {
                var source = new CompiledTachieSource(context, pool); source.UpdateCompiled(first); source.UpdateCompiled(null);
                Check(source.CurrentGeneration is null && pool.Snapshot().ActiveDocuments == 0);
                Bytes(new byte[8], Read(source.Output));
                source.UpdateCompiled(first); var image = source.Output; source.Dispose(); source.Dispose();
                Check(image.NativePointer == IntPtr.Zero && pool.Snapshot().ActiveDocuments == 0 && pool.Snapshot().ResidentDecodedBytes == 0);
                Throws<ObjectDisposedException>(() => source.UpdateCompiled(first));
            });
            foreach (var size in new[] { 2, 3 })
            {
                var pixels = Enumerable.Range(0, size * size).SelectMany(i => (i % 3) switch
                {
                    0 => new byte[] { 0, 0, 255, 255 }, 1 => new byte[] { 0, 255, 0, 255 },
                    _ => new byte[] { 255, 0, 0, 255 }
                }).ToArray();
                var path = Store(temp, (byte)(size + 10), size, size, pixels);
                Case($"asymmetric-{size}x{size}-compiled-pixels-exact-before-centering", () =>
                {
                    using var lease = pool.Acquire(Path.GetDirectoryName(path)!);
                    using var renderer = new TreeCompiledRenderer(context, lease);
                    renderer.Update(); Bytes(pixels, renderer.Readback());
                });
                Case($"asymmetric-{size}x{size}-centered-output-equals-direct-control", () =>
                {
                    var expected = ReferenceCentered(pixels, size, size);
                    if (size == 2) Bytes(pixels, expected);
                    using var source = new CompiledTachieSource(context, pool); source.UpdateCompiled(path);
                    Bytes(expected, Read(source.Output, size, size));
                });
            }

            var originalPath = Path.Combine(temp, "synthetic.psd"); PsdFixture.Write(originalPath);
            var repository = new CompiledAssetRepository(Path.Combine(temp, "original-cache"));
            using var preparation = new SourcePreparationService(repository);
            var originalRef = SourceAssetRef.Create(originalPath);
            var originalPixels = new byte[8 * 4 * 4]; var straight = PsdFixture.Pixels();
            // Independent expected canvas: one visible layer at (3,-2), clipped to 8x4, normal/opaque layer opacity.
            for (var y = 0; y < 2; y++) for (var x = 3; x < 8; x++)
            {
                var from = ((y + 2) * 8 + x - 3) * 4; var to = (y * 8 + x) * 4;
                originalPixels[to + 3] = straight[from + 3];
                for (var c = 0; c < 3; c++) originalPixels[to + c] = (byte)((straight[from + c] * straight[from + 3] + 127) / 255);
            }
            Case("original-paused-request-is-nonblocking-until-owner-applies", () =>
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                preparation.BeforeSnapshot = async token => { entered.SetResult(); await resume.Task.WaitAsync(token); };
                using var source = new CompiledTachieSource(context, pool, preparation);
                source.RequestOriginal(originalRef);
                entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Check(!source.PreparationCompletion.IsCompleted && source.CurrentGeneration is null);
                resume.SetResult(); source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                Check(source.CurrentGeneration is null && source.PreparationState == PreparationState.ReadyToRender);
                Check(source.ApplyReady()); Bytes(originalPixels, Read(source.Output, 8, 4));
                Check(source.PreparationState == PreparationState.DisplayedCurrent);
                preparation.BeforeSnapshot = null;
            });
            Case("original-exporting-usage-strict-cold-request-produces-correct-pixels", () =>
            {
                var coldRepository = new CompiledAssetRepository(Path.Combine(temp, "cold-export-cache"));
                using var cold = new SourcePreparationService(coldRepository);
                using var source = new CompiledTachieSource(context, pool, cold);
                ip.Source = originalRef;
                var timeline = new TimelineSourceDescription(new System.Drawing.Size(8, 4), new YukkuriMovieMaker.Player.Video.FrameTime(0, 30), new YukkuriMovieMaker.Player.Video.FrameTime(30, 30), 30, TimelineSourceUsage.Exporting, Guid.NewGuid(), []);
                var item = new TimelineItemSourceDescription(timeline, 0, 30, 0);
                var description = new TachieSourceDescription(item, new TachieDescription(cp, ip, []), 0, default);
                ((ITachieSource2)source).Update(description);
                Check(source.PreparationState == PreparationState.DisplayedCurrent && coldRepository.CompilationCount == 1);
                Bytes(originalPixels, Read(source.Output, 8, 4));
                var reads = pool.Snapshot().BlockReadCount; ((ITachieSource2)source).Update(description);
                Check(source.CompositionCount == 1 && pool.Snapshot().BlockReadCount == reads);
                cold.Invalidate(originalPath);
                Throws<IOException>(() => ((ITachieSource2)source).Update(description));
                Bytes(originalPixels, Read(source.Output, 8, 4));
            });
            Case("original-prepared-renderer-does-not-read-disk-during-compose", () =>
            {
                using var asset = preparation.PrepareAsync(originalRef, preparation.Revision(originalPath)).GetAwaiter().GetResult();
                using var prepared = asset.PrepareAppearance(pool); pool.Trim();
                var reads = pool.Snapshot().BlockReadCount;
                using var renderer = new TreeCompiledRenderer(context, prepared);
                renderer.UpdatePrepared(prepared); Bytes(originalPixels, renderer.Readback());
                Check(reads == pool.Snapshot().BlockReadCount);
                Check(!renderer.UpdatePrepared(prepared)); Check(reads == pool.Snapshot().BlockReadCount);
            });
            Case("parameter-original-reference-json-undo-relink-and-materials", () =>
            {
                var parameter = new CompiledItemParameter(); var undo = new UndoRedoManager(); undo.Subscribe(parameter);
                var commands = 0; parameter.UndoRedoCommandCreated += (_, _) => commands++;
                parameter.File = originalPath; undo.Record(); var identity = parameter.Source!.AssetIdentity;
                var changed = Path.Combine(temp, "relocated.psd"); parameter.ReplaceFile(originalPath, changed); undo.Record();
                Check(parameter.Source!.AssetIdentity == identity && ((IFileItem)parameter).GetFiles().Single() == changed);
                undo.UndoAsync().GetAwaiter().GetResult(); Check(parameter.File == originalPath);
                var json = JsonConvert.SerializeObject(parameter);
                var reloaded = JsonConvert.DeserializeObject<CompiledItemParameter>(json)!;
                Check(reloaded.Source == parameter.Source && ((IFileItem)reloaded).GetFiles().Single() == originalPath);
                Check(!json.Contains("manifest.json") && !json.Contains("ContentKey") && !json.Contains("GenerationId"));
                var before = commands;
                using var source = new CompiledTachieSource(context, pool, preparation); source.RequestOriginal(parameter.Source!);
                source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); source.ApplyReady();
                Check(commands == before && parameter.Source!.AssetIdentity == identity);
                undo.UnSubscribe(parameter);
            });
            Case("unique-parser-identity-coexists-with-bundled-parser", () =>
            {
                var bundled = System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "PsdParser.dll"));
                Check(bundled.GetName().Name == "PsdParser");
                Check(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "PsdTachieNext.Parser"));
                Check(typeof(PsdCompiler).Assembly.GetReferencedAssemblies().Any(a => a.Name == "PsdTachieNext.Parser"));
            });
            Case("equivalent-refresh-clone-preserves-original-identity-and-source", () =>
            {
                var parameter = new CompiledItemParameter { Source = originalRef };
                var persisted = JsonConvert.SerializeObject(parameter); var commands = 0;
                parameter.UndoRedoCommandCreated += (_, _) => commands++;
                using var source = new CompiledTachieSource(context, pool, preparation);
                var timeline = new TimelineSourceDescription(new System.Drawing.Size(8,4), new FrameTime(0,30), new FrameTime(30,30),30,TimelineSourceUsage.Paused,Guid.NewGuid(),[]);
                TachieSourceDescription Description(CompiledItemParameter p) => new(new TimelineItemSourceDescription(timeline,0,30,0),new TachieDescription(cp,p,[]),0,default);
                source.Update(Description(parameter)); source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                source.Update(Description(parameter)); var pointer = source.Output.NativePointer;
                var compositions = source.CompositionCount; var reads = pool.Snapshot().BlockReadCount; var compiles = preparation.CompilationCount;
                var slot = (ITachieItemParameter)parameter;
                Check(CompiledParameterRefreshBridge.TryReplaceEquivalent(parameter,()=>slot,p=>slot=p,out var clone));
                Check(clone is not null && !ReferenceEquals(parameter,clone) && clone.Source == originalRef);
                Check(JsonConvert.SerializeObject(clone) == persisted && clone!.RefreshRevision == parameter.RefreshRevision && commands == 0);
                source.Update(Description(clone!));
                Check(source.Output.NativePointer == pointer && source.CompositionCount == compositions);
                Check(pool.Snapshot().BlockReadCount == reads && preparation.CompilationCount == compiles);
                Check(!CompiledParameterRefreshBridge.TryReplaceEquivalent(parameter,()=>slot,p=>slot=p,out _));
            });
            Case("refresh-owner-rejects-parameter-undo-ABA-and-deletion", () =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var parameter = new CompiledItemParameter { Source = originalRef };
                    var character = new Character { TachieType = typeof(CompiledTachiePlugin), TachieCharacterParameter = cp,
                        TachieDefaultItemParameter = parameter, TachieDefaultFaceParameter = fp };
                    var item = new YukkuriMovieMaker.Project.Items.TachieItem(character) { TachieItemParameter = parameter };
                    using var registration = new HostPreparationBridge.Owner(item);
                    var epoch = registration.Lifetime.Capture(); var revision = parameter.RefreshRevision;
                    var undo = new UndoRedoManager(); undo.Subscribe(item);
                    item.TachieItemParameter = new CompiledItemParameter { Source = originalRef }; undo.Record();
                    undo.UndoAsync().GetAwaiter().GetResult();
                    Check(ReferenceEquals(item.TachieItemParameter, parameter));
                    Check(!registration.Lifetime.IsCurrent(epoch));
                    Check(!CompiledParameterRefreshBridge.TryReplaceEquivalent(parameter,()=>item.TachieItemParameter,p=>item.TachieItemParameter=p,out _,()=>registration.Lifetime.IsCurrent(epoch)));
                    undo.UnSubscribe(item);
                    epoch = registration.Lifetime.Capture();
                    parameter.File = Path.Combine(temp,"B.psd"); parameter.File = originalPath;
                    Check(parameter.Source == originalRef && parameter.RefreshRevision > revision);
                    Check(!registration.Lifetime.IsCurrent(epoch));
                    epoch = registration.Lifetime.Capture(); registration.Dispose();
                    Check(!registration.Lifetime.IsCurrent(epoch)); Check(ReferenceEquals(item.TachieItemParameter,parameter));
                });
            });
            Case("same-parameter-source-ABA-starts-a-new-request-before-publication", () =>
            {
                using var service = new SourcePreparationService(new CompiledAssetRepository(Path.Combine(temp,"parameter-aba-cache")));
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                service.BeforeSnapshot = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
                using var source = new CompiledTachieSource(context,pool,service);
                var parameter = new CompiledItemParameter { Source=originalRef };
                var timeline = new TimelineSourceDescription(new System.Drawing.Size(8,4),new FrameTime(0,30),new FrameTime(30,30),30,TimelineSourceUsage.Paused,Guid.NewGuid(),[]);
                var description = new TachieSourceDescription(new TimelineItemSourceDescription(timeline,0,30,0),new TachieDescription(cp,parameter,[]),0,default);
                try
                {
                    source.Update(description); entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    var old = source.RefreshRequest;
                    parameter.File=Path.Combine(temp,"B.psd"); parameter.File=originalPath;
                    source.Update(description); var latest=source.RefreshRequest;
                    Check(latest.AssetRequestId>old.AssetRequestId && latest.SettingsRevision>old.SettingsRevision);
                    Check(source.CurrentGeneration is null);
                    release.TrySetResult(); source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    source.Update(description); Check(source.RefreshRequest==latest && source.CompositionCount==1);
                    Bytes(originalPixels,Read(source.Output,8,4));
                }
                finally { release.TrySetResult(); }
            });
            Case("independent-live-owner-retirement-does-not-retire-another-owner", () =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var parameter = new CompiledItemParameter { Source=originalRef };
                    var character = new Character { TachieType=typeof(CompiledTachiePlugin),TachieCharacterParameter=cp,
                        TachieDefaultItemParameter=parameter,TachieDefaultFaceParameter=fp };
                    var firstItem = new YukkuriMovieMaker.Project.Items.TachieItem(character) { TachieItemParameter=parameter };
                    var secondItem = new YukkuriMovieMaker.Project.Items.TachieItem(character) { TachieItemParameter=parameter };
                    using var firstOwner = new HostPreparationBridge.Owner(firstItem); using var secondOwner = new HostPreparationBridge.Owner(secondItem);
                    var firstStamp=firstOwner.Lifetime.Capture(); var secondStamp=secondOwner.Lifetime.Capture();
                    firstOwner.Dispose(); Check(!firstOwner.Lifetime.IsCurrent(firstStamp)); Check(secondOwner.Lifetime.IsCurrent(secondStamp));
                    parameter.File=Path.Combine(temp,"shared-owner-change.psd"); Check(!secondOwner.Lifetime.IsCurrent(secondStamp));
                });
            });
            Case("original-failure-recovery-retains-previous-output-and-explicit-reload", () =>
            {
                using var source = new CompiledTachieSource(context, pool, preparation); preparation.ManualHints = true;
                source.RequestOriginal(originalRef); source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); source.ApplyReady();
                var missing = SourceAssetRef.Create(Path.Combine(temp, "recover.psd")); source.RequestOriginal(missing);
                source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Check(source.PreparationState == PreparationState.FailedWithPrevious);
                Check(source.PreparationDiagnostic!.Recovery == PreparationRecovery.LocateSource);
                Check(source.PreparationDiagnostic.HasPreviousOutput);
                Bytes(originalPixels, Read(source.Output, 8, 4));
                PsdFixture.Write(missing.Path); source.RetryOriginal();
                source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); Check(source.ApplyReady());
                Check(source.PreparationDiagnostic is null && source.PreparationState == PreparationState.DisplayedCurrent);
                Bytes(originalPixels, Read(source.Output, 8, 4));
            });
            Case("strict-source-dispose-cancels-wait-before-acquiring-owner", () =>
            {
                var repo = new CompiledAssetRepository(Path.Combine(temp, "dispose-cache")); using var service = new SourcePreparationService(repo);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                service.BeforeSnapshot = async token =>
                { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, token); } finally { canceled.SetResult(); } };
                using var source = new CompiledTachieSource(context, pool, service);
                var parameter = new CompiledItemParameter { Source = originalRef };
                var timeline = new TimelineSourceDescription(new System.Drawing.Size(8, 4), new FrameTime(0, 30), new FrameTime(30, 30), 30, TimelineSourceUsage.Exporting, Guid.NewGuid(), []);
                var description = new TachieSourceDescription(new TimelineItemSourceDescription(timeline, 0, 30, 0), new TachieDescription(cp, parameter, []), 0, default);
                var strict = Task.Run(() => ((ITachieSource2)source).Update(description));
                entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                source.Dispose();
                Throws<InvalidOperationException>(() => strict.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
                canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Check(source.PreparationState == PreparationState.Disposed && source.Output.NativePointer == IntPtr.Zero);
            });
            Case("original-revision-after-enddraw-rejects-publication-and-keeps-output", () =>
            {
                var snapshots = 0; preparation.BeforeSnapshot = _ => { Interlocked.Increment(ref snapshots); return Task.CompletedTask; };
                using var source = new CompiledTachieSource(context, pool, preparation);
                source.RequestOriginal(originalRef); source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); source.ApplyReady();
                var old = source.CurrentGeneration; source.RequestOriginal(originalRef);
                source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Task? changing = null;
                void Hint(string _, long revision) => invalidated.TrySetResult();
                preparation.SourceChanged += Hint;
                try
                {
                    source.BeforeOriginalPublication = () =>
                    { changing = Task.Run(() => preparation.Invalidate(originalPath)); invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); };
                    Check(!source.ApplyReady());
                    Check(old == source.CurrentGeneration); Bytes(originalPixels, Read(source.Output, 8, 4));
                }
                finally
                {
                    source.BeforeOriginalPublication = null; preparation.SourceChanged -= Hint;
                    try { changing?.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                    finally { preparation.BeforeSnapshot = null; }
                }
                Check(source.ApplyReady()); Check(snapshots == 3);
            });
            Case("paused-owner-update-adopts-retried-revision-without-restarting", () =>
            {
                var path = Path.Combine(temp, "retry-source.psd"); PsdFixture.Write(path, seed: 73);
                var repository = new CompiledAssetRepository(Path.Combine(temp, "retry-cache")); using var service = new SourcePreparationService(repository);
                service.ManualHints = true; var attempts = 0;
                service.AfterSnapshot = (_, _) =>
                { if (++attempts == 1) { PsdFixture.Write(path); service.Invalidate(path); } return Task.CompletedTask; };
                using var source = new CompiledTachieSource(context, pool, service); var reference = SourceAssetRef.Create(path);
                source.RequestOriginal(reference); source.PreparationCompletion.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                Check(attempts == 2 && source.PreparationState == PreparationState.ReadyToRender);
                var parameter = new CompiledItemParameter { Source = reference };
                var timeline = new TimelineSourceDescription(new System.Drawing.Size(8, 4), new FrameTime(0, 30), new FrameTime(30, 30), 30, TimelineSourceUsage.Paused, Guid.NewGuid(), []);
                ((ITachieSource2)source).Update(new TachieSourceDescription(new TimelineItemSourceDescription(timeline, 0, 30, 0), new TachieDescription(cp, parameter, []), 0, default));
                Check(source.PreparationState == PreparationState.DisplayedCurrent && attempts == 2);
                Bytes(originalPixels, Read(source.Output, 8, 4));
            });

            CompiledTachieSource.ProofPreparation = preparation;
            try { NativeModelProof.Run(output, Case, Check); }
            finally { CompiledTachieSource.ProofPreparation = null; }

            ID2D1Bitmap1 Target(int width, int height) => context.CreateBitmap(new SizeI(width, height), new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
            byte[] Read(ID2D1Image image, int width = 2, int height = 1)
            {
                using var target = Target(width, height);
                context.Target = target; context.Transform = Matrix3x2.CreateTranslation(width / 2f, height / 2f);
                context.BeginDraw();
                try { context.Clear(new Color4(0, 0, 0, 0)); context.DrawImage(image); }
                finally { context.EndDraw().CheckError(); context.Target = null; context.Transform = Matrix3x2.Identity; }
                return ReadBitmap(target);
            }
            byte[] ReadBitmap(ID2D1Bitmap1 bitmap)
            {
                var size = bitmap.PixelSize; var stride = checked(size.Width * 4);
                using var cpu = context.CreateBitmap(size, new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
                cpu.CopyFromBitmap(bitmap).CheckError(); var map = cpu.Map(MapOptions.Read);
                try
                {
                    var bytes = new byte[checked(stride * size.Height)];
                    for (var y = 0; y < size.Height; y++) Marshal.Copy(map.Bits + checked(y * (int)map.Pitch), bytes, y * stride, stride);
                    return bytes;
                }
                finally { cpu.Unmap(); }
            }
            byte[] ReferenceCentered(byte[] opaquePixels, int width, int height)
            {
                if (opaquePixels.Length != width * height * 4 || Enumerable.Range(0, width * height).Any(i => opaquePixels[i * 4 + 3] != 255))
                    throw new ArgumentException("Independent reference expects known opaque BGRA pixels.");
                var memory = Marshal.AllocHGlobal(opaquePixels.Length);
                try
                {
                    Marshal.Copy(opaquePixels, 0, memory, opaquePixels.Length);
                    using var bitmap = context.CreateBitmap(new SizeI(width, height), memory, checked(width * 4), new BitmapProperties1(
                        new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96));
                    using var transform = new AffineTransform2D(context) { TransformMatrix = Matrix3x2.CreateTranslation(-width / 2f, -height / 2f) };
                    transform.SetInput(0, bitmap, true); using var image = transform.Output;
                    try { return Read(image, width, height); }
                    finally { transform.SetInput(0, null, true); }
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
        }
        catch (Exception ex) { failures++; results.Add(new { name = "host-device-or-harness", status = "FAIL", error = ex.ToString() }); }
        finally
        {
            try { Directory.Delete(temp, true); }
            catch (Exception ex) { failures++; results.Add(new { name = "cleanup", status = "FAIL", error = ex.ToString() }); }
        }
        var assembly = typeof(CompiledTachieSource).Assembly; var status = failures == 0 ? "PASS" : "FAIL";
        var result = new
        {
            schema = "psd-next.host-callback-proof.v2", status, total = results.Count, failures, assertions, tolerance = 0,
            realYmm4Process = true, timelineUiVerified = false, physicalGpuPerformanceClaim = false,
            centeringReference = "Known opaque pixels -> default Direct2D affine center -> same readback; not full YMM4 renderer comparison",
            sourceHead = Environment.GetEnvironmentVariable("SOURCE_HEAD"),
            productAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant(),
            hostApi = typeof(ITachieSource).Assembly.FullName, graphicsAssembly = typeof(ID2D1DeviceContext).Assembly.FullName,
            observations, results
        };
        File.WriteAllText(Path.Combine(output, "host-results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "host-complete.txt"), status);
        void Case(string name, Action action)
        {
            try { action(); results.Add(new { name, status = "PASS" }); }
            catch (Exception ex) { failures++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); }
        }
        void Check(bool value) { assertions++; if (!value) throw new Exception("Assertion failed."); }
        void Bytes(byte[] expected, byte[] actual)
        {
            assertions++; if (!expected.AsSpan().SequenceEqual(actual))
                throw new Exception($"Expected {Convert.ToHexString(expected)}; got {Convert.ToHexString(actual)}");
        }
        void Throws<T>(Action action) where T : Exception
        { assertions++; try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    }
    private static string Store(string root, byte seed, int width = 2, int height = 1, byte[]? firstPixels = null)
    {
        var dir = Path.Combine(root, "compiled-" + seed); using var writer = new CompiledStoreWriter(dir);
        var a = writer.AddBlock(BlockFormat.Bgra8Straight, width, height, firstPixels ??
            (seed == 0 ? new byte[] { 0,0,255,255,0,0,255,255 } : new byte[] { 0,255,0,255,0,255,0,255 }), compress: false);
        var other = Enumerable.Range(0, width * height).SelectMany(_ => new byte[] { 255, 0, 0, 255 }).ToArray();
        var b = writer.AddBlock(BlockFormat.Bgra8Straight, width, height, other, compress: false);
        var raw = new byte[26]; raw[0] = seed; var identity = new SourceFingerprint(CompiledFormat.Hash(raw), 26);
        var nodes = ImmutableArray.Create(
            new LayerNode(0,null,0,NodeKind.Layer,"first",new(0,0,width,height),0,true,"norm",255,false,a,null,[]),
            new LayerNode(1,null,1,NodeKind.Layer,"second",new(0,0,width,height),2,false,"norm",255,false,b,null,[]));
        writer.Complete(dir, new CompiledManifest(1,CompiledFormat.CompilerId,CompiledFormat.Generation(identity),identity,1,
            width,height,8,3,null,null,nodes,writer.Blocks));
        return Path.Combine(dir, "manifest.json");
    }
}
