using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using PsdTachieNext.Tests;
using PsdTachieNext.Ymm4;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using NativeJson = YukkuriMovieMaker.Json.Json;

namespace PsdTachieNext.HostProof;

// Detached native project and public factory evidence only; no live player or video job claim.
internal static class NativeModelProof
{
    internal static void Run(string output, Action<string, Action> test, Action<bool> check)
    {
        var directory = Path.Combine(output, "synthetic-project"); Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "original.psd"); PsdFixture.Write(original);
        var moved = Path.Combine(directory, "relocated.psd"); File.Copy(original, moved);
        var path = Path.Combine(directory, "source.ymmp");
        var character = new Character { Name = "PR-A synthetic", TachieType = typeof(CompiledTachiePlugin),
            TachieCharacterParameter = new CompiledCharacterParameter(),
            TachieDefaultItemParameter = new CompiledItemParameter { File = original },
            TachieDefaultFaceParameter = new CompiledFaceParameter() };
        var item = new TachieItem(character) { Frame = 0, Length = 30, Layer = 0,
            TachieItemParameter = new CompiledItemParameter { File = original } };
        var timeline = new Timeline { Name = "PR-A synthetic", Items = ImmutableList.Create<IItem>(item) };
        timeline.VideoInfo.Width = 64; timeline.VideoInfo.Height = 32; timeline.VideoInfo.FPS = 30;
        timeline.VideoInfo.BackgroundColor = System.Windows.Media.Colors.Black;
        timeline.RefreshTimelineLengthAndMaxLayer();
        var project = new Project(new[] { character }, path); project.Timelines.Clear(); project.Timelines.Add(timeline);
        Project? loaded = null;
        test("native-detached-ymmp-save-load-original-reference-and-materials", () =>
        {
            NativeJson.Save(project, path, null);
            loaded = NativeJson.Load<Project>(path) ?? throw new InvalidDataException("Native project load returned null.");
            check(!ReferenceEquals(project, loaded));
            var restored = loaded.Timelines.Single().Items.OfType<TachieItem>().Single();
            var before = (CompiledItemParameter)item.TachieItemParameter;
            var after = (CompiledItemParameter)restored.TachieItemParameter;
            check(after.Source == before.Source);
            check(restored.GetFiles().Contains(original));
            check(restored.GetResources().Any());
            check(loaded.Characters.Single().TachieType == typeof(CompiledTachiePlugin));
            var json = File.ReadAllText(path);
            check(!json.Contains("manifest.json") && !json.Contains("ContentKey") && !json.Contains("GenerationId"));
        });
        test("native-detached-ymmp-relink-save-reopen-preserves-source-identity", () =>
        {
            var sourceHash = SHA256.HashData(File.ReadAllBytes(path));
            var restored = loaded!.Timelines.Single().Items.OfType<TachieItem>().Single();
            var identity = ((CompiledItemParameter)restored.TachieItemParameter).Source!.AssetIdentity;
            restored.ReplaceFile(original, moved);
            loaded.Characters.Single().ReplaceFile(original, moved);
            var archive = Path.Combine(directory, "relocated.ymmp"); NativeJson.Save(loaded, archive, null);
            var reopened = NativeJson.Load<Project>(archive) ?? throw new InvalidDataException("Native project reopen returned null.");
            var parameter = (CompiledItemParameter)reopened.Timelines.Single().Items.OfType<TachieItem>().Single().TachieItemParameter;
            check(parameter.File == moved && parameter.Source!.AssetIdentity == identity);
            check(sourceHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
        });
        test("native-detached-scene-public-factory-exporting-render-dispose-recreate", () =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var scenes = new Scenes(false) { Timelines = ImmutableList.Create(timeline) };
                var scene = new Scene(timeline, scenes, Array.Empty<Guid>());
                byte[]? first = null;
                long? compiledAfterFirst = null;
                for (var iteration = 0; iteration < 2; iteration++)
                {
                    check(scene.TryCreateVideoSource(out var source));
                    using (source)
                    {
                        check(source is not null);
                        source!.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting);
                        var compiledNow = CompiledTachieSource.DefaultCompilationCount;
                        if (compiledAfterFirst is null) compiledAfterFirst = compiledNow;
                        else check(compiledAfterFirst.Value == compiledNow);
                        using var image = source.RenderBitmapMemoryStream();
                        var bytes = image.ToArray(); check(bytes.Length > 0);
                        image.Position = 0;
                        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(image,
                            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoder.Frames[0],
                            System.Windows.Media.PixelFormats.Pbgra32, null, 0);
                        check(bitmap.PixelWidth == 64 && bitmap.PixelHeight == 32);
                        var actual = new byte[64 * 32 * 4]; bitmap.CopyPixels(actual, 64 * 4, 0);
                        // Native PNG helper emits RGB. Use an explicitly opaque black scene;
                        // the independent expected value is source-over-black, not transparent PNG alpha.
                        var expected = new byte[actual.Length]; var straight = PsdFixture.Pixels();
                        for (var pixel = 0; pixel < 64 * 32; pixel++) expected[pixel * 4 + 3] = 255;
                        for (var y = 0; y < 2; y++) for (var x = 3; x < 8; x++)
                        {
                            var from = ((y + 2) * 8 + x - 3) * 4; var to = ((y + 14) * 64 + x + 28) * 4;
                            for (var c = 0; c < 3; c++) expected[to + c] = (byte)((straight[from + c] * straight[from + 3] + 127) / 255);
                        }
                        File.WriteAllBytes(Path.Combine(directory, $"native-frame-{iteration}.png"), bytes);
                        if (!expected.AsSpan().SequenceEqual(actual))
                            throw new InvalidDataException($"Native frame pixels differ in {actual.Where((v, i) => v != expected[i]).Count()} bytes.");
                        check(true);
                        if (first is null) first = bytes; else check(first.AsSpan().SequenceEqual(bytes));
                        File.WriteAllText(Path.Combine(directory, "native-factory-scope.json"),
                            System.Text.Json.JsonSerializer.Serialize(new { scope = "detached-public-scene-factory",
                                sourceType = source.GetType().FullName, devicesType = source.Devices.GetType().FullName,
                                contextType = source.Devices.DeviceContext.GetType().FullName,
                                usage = "Exporting followed by Playing", livePlayerVerified = false,
                                coldCacheVerified = false, normalVideoJobVerified = false, pixelTolerance = 0,
                                pixelReference = "Known synthetic straight BGRA, clipped/centered and source-over opaque black; native RGB PNG decoded to opaque Pbgra32" }));
                        source.Update(TimeSpan.FromSeconds(1d / 30), TimelineSourceUsage.Playing);
                    }
                }
            });
        });
        test("native-detached-scene-exporting-missing-original-error-boundary", () =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var bad = new TachieItem(character) { Frame = 0, Length = 30, Layer = 0,
                    TachieItemParameter = new CompiledItemParameter { File = Path.Combine(directory, "missing.psd") } };
                var failedTimeline = new Timeline { Items = ImmutableList.Create<IItem>(bad) };
                failedTimeline.VideoInfo.Width = 64; failedTimeline.VideoInfo.Height = 32;
                failedTimeline.RefreshTimelineLengthAndMaxLayer();
                var scenes = new Scenes(false) { Timelines = ImmutableList.Create(failedTimeline) };
                var scene = new Scene(failedTimeline, scenes, Array.Empty<Guid>());
                check(scene.TryCreateVideoSource(out var source));
                using (source)
                {
                    Exception? error = null;
                    try { source!.Update(TimeSpan.Zero, TimelineSourceUsage.Exporting); }
                    catch (Exception ex) { error = ex; }
                    File.WriteAllText(Path.Combine(directory, "native-exporting-error-boundary.txt"),
                        error?.ToString() ?? "Update returned normally despite missing original. Normal video job interruption remains OPEN.");
                    check(error is not null);
                }
            });
        });
    }
}
