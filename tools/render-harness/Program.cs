// Development-only Phase 2 QA harness: drives the ported H3 ViewModels directly —
// the same instances, the same BuildWorkflow patches, the same submit/collect/join
// pipeline the on-screen app runs when Generate is clicked. The Avalonia layer was
// smoke-tested separately (FLIPPIX_SMOKE); the VM layer here needs no UI thread.
//
// Usage:  dotnet run --project tools/render-harness -c Release -- <stage>
// Stages: i2v | fflf | character | chain | cast | hybrid | ensemble
//
// Asset notes:
//   i2v / character / cast / hybrid / ensemble reuse kickboxing stills from
//   ~/Pictures/flippix-images (previous Qwen generations on this install).
//   fflf pairs two of them as first/last frame.
//   chain synthesizes a 24 s tone track with ffmpeg (no music on this box).
//   cast builds a REAL character sheet (Qwen-Image-Edit pass); hybrid/ensemble
//   set UseSourceAsSheet to keep those runs to the video graph only.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using FlipPix.ComfyUI.Http;
using FlipPix.ComfyUI.WebSocket;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels.Video;

var services = new ServiceCollection();
ConfigureServices(services);
var sp = services.BuildServiceProvider();

var logger = sp.GetRequiredService<IAppLogger>();
var settingsService = sp.GetRequiredService<SettingsService>();
settingsService.SetLogger(logger);
VramContext.Configure(settingsService.Settings.VramTier, settingsService.Settings.DetectedVramGb);
Console.WriteLine($"[harness] ComfyUI={settingsService.Settings.BaseUrl}  vram tier={VramContext.EffectiveTier}");

var imgA = "/home/x2/Pictures/flippix-images/woman_kickboxing-qwenvl-11.png";
var imgB = "/home/x2/Pictures/flippix-images/woman_kickboxing-qwenvl-12.png";
var stage = args.Length > 0 ? args[0] : "list";
var sw = Stopwatch.StartNew();

switch (stage)
{
    case "i2v":       await RunI2V(sp, imgA); break;
    case "fflf":      await RunFflf(sp, imgA, imgB); break;
    case "character": await RunCharacter(sp, imgA, imgB); break;
    case "chain":     await RunChain(sp, imgA); break;
    case "cast":      await RunCast(sp, imgA, imgB); break;
    case "hybrid":    await RunHybrid(sp, imgA); break;
    case "ensemble":  await RunEnsemble(sp, imgA); break;
    case "resolver":  await RunResolver(sp); break;
    default:
        Console.WriteLine("stages: i2v | fflf | character | chain | cast | hybrid | ensemble | resolver");
        return;
}
Console.WriteLine($"[harness] {stage} finished in {sw.Elapsed.TotalMinutes:0.0} min");

// ───────────────────────────────────────────────────────────────────────────────

static void ConfigureServices(IServiceCollection services)
{
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
    services.AddHttpClient<ComfyUIHttpClient>(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(10);
        client.MaxResponseContentBufferSize = 500 * 1024 * 1024;
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        MaxRequestContentBufferSize = 500 * 1024 * 1024
    });

    services.AddSingleton<SettingsService>();
    services.AddSingleton<IAppLogger, FileLogger>();
    services.AddSingleton<VideoAnalysisService>();
    services.AddSingleton<ImageAnalysisService>();
    services.AddSingleton<IFileDialogService, FileDialogService>();
    services.AddSingleton<WindowPositionService>();
    services.AddSingleton<LoraManager>();
    services.AddSingleton<ComfyUIImageRetriever>();
    services.AddSingleton<GenerationProgressTracker>();
    services.AddSingleton<ChunkPromptCacheService>();
    services.AddSingleton<ScenePromptLibrary>();
    services.AddSingleton<FlipPix.UI.Linux.Services.ModelInstallerService>();
    services.AddSingleton<FlipPix.Core.Interfaces.IMissingModelResolver, FlipPix.UI.Linux.Services.MissingModelResolver>();
    services.AddSingleton<FlipPix.UI.Linux.Services.NodeInstallerService>();
    services.AddSingleton<FlipPix.Core.Interfaces.IMissingNodeResolver, FlipPix.UI.Linux.Services.MissingNodeResolver>();
    services.AddHttpClient<LMStudioService>();
    services.AddHttpClient<OllamaService>();
    services.AddSingleton<IPromptService, PromptService>();
    services.AddSingleton<LMStudioService>(p =>
    {
        var http = p.GetRequiredService<IHttpClientFactory>().CreateClient();
        return new LMStudioService(http, p.GetRequiredService<IAppLogger>(),
            () => p.GetRequiredService<SettingsService>().Settings.LMStudioSettings?.BaseUrl ?? "http://localhost:8080");
    });
    services.AddSingleton<ComfyUISettings>(p => p.GetRequiredService<SettingsService>().Settings);
    services.AddSingleton<ComfyUIHttpClient>(p =>
    {
        var http = p.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ComfyUIHttpClient));
        return new ComfyUIHttpClient(http, p.GetRequiredService<IAppLogger>(), p.GetRequiredService<ComfyUISettings>());
    });
    services.AddSingleton<ComfyUIWebSocketClient>(p =>
        new ComfyUIWebSocketClient(p.GetRequiredService<IAppLogger>(), p.GetRequiredService<ComfyUISettings>().BaseUrl));
    services.AddSingleton<FlipPix.ComfyUI.Services.ComfyUIService>();
    services.AddSingleton<WorkflowQueueCoordinator>();
}

static T NewVM<T>(IServiceProvider sp) where T : VideoProcessingBaseViewModel
{
    var ctor = typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
    var args = ctor.GetParameters().Select(p =>
    {
        var found = sp.GetService(p.ParameterType);
        if (found == null && p.HasDefaultValue) return p.DefaultValue;
        return found;
    }).ToArray();
    return (T)ctor.Invoke(args);
}

// The preview loaders are UI-only; the harness passes delegates that render nothing,
// keeping it headless.

static async Task RunI2V(IServiceProvider sp, string image)
{
    var vm = NewVM<MiniMaxI2VViewModel>(sp);
    vm.References[0].Path = image;
    vm.Prompt = "A single martial artist in a bright dojo practices a focused kicking combination, " +
                "camera slowly orbiting right, natural daylight, crisp realistic motion.";
    vm.LengthSeconds = 6;
    Console.WriteLine($"[i2v] CanGenerate={vm.CanGenerate}");
    if (!vm.CanGenerate) { Fail("i2v", vm.GenerateBlockedReason); return; }

    vm.GenerateCommand.Execute(null);
    await WaitIdle(vm, "i2v", busy: v => v.IsProcessing || v.IsProcessingQueue,
        status: v => v.QueueStatus, minutes: 12);
    Report("i2v", vm);
}

static async Task RunFflf(IServiceProvider sp, string first, string last)
{
    var vm = NewVM<MiniMaxFflfViewModel>(sp);
    vm.PrimaryReferencePath = first;
    vm.Clips[0].EndFrame.Path = last;
    vm.Clips[0].Seconds = 5;
    vm.Clips[0].Prompt = "The kickboxer steps forward and lands a clean roundhouse kick, gym lights " +
                         "glinting off the mats, steady camera.";
    Console.WriteLine($"[fflf] chain: {vm.ChainSummary}  CanGenerate={vm.CanGenerate}");
    if (!vm.CanGenerate) { Fail("fflf", vm.GenerateBlockedReason); return; }

    vm.GenerateCommand.Execute(null);
    await WaitIdle(vm, "fflf", busy: v => v.IsProcessing || v.IsProcessingQueue,
        status: v => v.QueueStatus, minutes: 12);
    Report("fflf", vm);
}

static async Task RunCharacter(IServiceProvider sp, string character, string scene)
{
    var vm = NewVM<MiniMaxCharacterViewModel>(sp);
    vm.Character1Path = character;
    vm.SceneImagePath = scene;   // normally analyzed by the LLM; typed prompt below is equivalent
    vm.Prompt = "<subject> trains alone in a sunlit gym, throwing sharp combinations at a heavy bag, " +
                "confident and focused, handheld camera following her movement.";
    Console.WriteLine($"[character] CanGenerate={vm.CanGenerate}");
    if (!vm.CanGenerate) { Fail("character", "prompt/character missing"); return; }

    vm.GenerateCommand.Execute(null);
    await WaitIdle(vm, "character", busy: v => v.IsProcessing || v.IsProcessingQueue,
        status: v => v.QueueStatus, minutes: 12);
    Report("character", vm);
}

static async Task RunChain(IServiceProvider sp, string reference)
{
    // No music on this box — synthesize a short tone track so the mux path has a song.
    var audio = "/tmp/flippix-h3chain-tone.wav";
    var ffmpeg = WhichFfmpeg();
    Process.Start(ffmpeg, $"-y -f lavfi -i \"sine=frequency=220:duration=24\" -ar 44100 {audio}")!.WaitForExit();
    if (!File.Exists(audio)) { Fail("chain", "ffmpeg could not synthesize audio"); return; }

    var vm = NewVM<H3ChainViewModel>(sp);
    vm.Reference1Path = reference;
    vm.AudioPath = audio;
    vm.Prompt = "Segment 1: the athlete stretches and warms up in a quiet gym.\n\n" +
                "Segment 2: she throws a fast combination of strikes, sweat visible, drive beat.";
    Console.WriteLine($"[chain] segments={vm.PromptSegmentCount} planned={vm.PlannedSegmentCount} CanGenerate={vm.CanGenerate}");
    if (!vm.CanGenerate) { Fail("chain", "reference/audio/prompt missing"); return; }

    vm.GenerateCommand.Execute(null);
    await WaitIdle(vm, "chain", busy: v => v.IsProcessing || v.IsProcessingQueue,
        status: v => v.QueueStatus, minutes: 20);
    Report("chain", vm);
}

static async Task RunCast(IServiceProvider sp, string character, string scene)
{
    var vm = NewVM<H3CastViewModel>(sp);
    vm.Character1.SourcePath = character;
    vm.SceneImagePath = scene;
    vm.Prompt = "<1> drills a sharp kicking combination in a bright modern gym while the camera " +
                "orbits slowly; others train blurred in the background.";
    Console.WriteLine($"[cast] building character sheet (Qwen-Image-Edit pass)…");
    if (!vm.CanBuildSheets) { Fail("cast", "CanBuildSheets false"); return; }
    vm.BuildSheetsCommand.Execute(null);

    var sheetSw = Stopwatch.StartNew();
    while (vm.IsBuildingSheets && sheetSw.Elapsed < TimeSpan.FromMinutes(10))
    {
        await Task.Delay(3000);
        Console.WriteLine($"[cast sheets +{sheetSw.Elapsed.TotalSeconds:0}s] {vm.SheetPhase}");
    }
    Console.WriteLine($"[cast] AllSheetsReady={vm.AllSheetsReady} CanGenerate={vm.CanGenerate}");
    if (!vm.AllSheetsReady) { Fail("cast", "sheet build did not finish"); return; }
    if (!vm.CanGenerate) { Fail("cast", "prompt missing"); return; }

    vm.GenerateCommand.Execute(null);
    await WaitIdle(vm, "cast", busy: v => v.IsProcessing || v.IsProcessingQueue,
        status: v => v.QueueStatus, minutes: 20);
    Report("cast", vm);
}

static async Task RunHybrid(IServiceProvider sp, string character)
{
    var vm = NewVM<H3CastHybridViewModel>(sp);
    vm.Character1.SourcePath = character;
    vm.Character1.UseSourceAsSheet = true;   // sheet pipeline exercised on the Cast stage
    vm.Prompt = "<1> trains with focused intensity in a sunlit gym, sharp strikes, orbiting camera.";
    Console.WriteLine($"[hybrid] sheetsReady={vm.AllSheetsReady} CanGenerate={vm.CanGenerate}");
    if (!vm.CanGenerate) { Fail("hybrid", "CanGenerate false"); return; }

    vm.GenerateCommand.Execute(null);
    await WaitIdle(vm, "hybrid", busy: v => v.IsProcessing || v.IsProcessingQueue,
        status: v => v.QueueStatus, minutes: 20);
    Report("hybrid", vm);
}

static async Task RunEnsemble(IServiceProvider sp, string character)
{
    var vm = NewVM<H3EnsembleViewModel>(sp);
    var slot = new CharacterSlot(1, (string path, out string info) => { info = ""; return null; }, () => { });
    slot.SourcePath = character;
    slot.UseSourceAsSheet = true;
    vm.Cast.Add(slot);
    vm.Prompt = "<1> drills combinations in a busy gym at golden hour; the crowd blurs past behind her.";
    Console.WriteLine($"[ensemble] cast={vm.Cast.Count} CanGenerate={vm.CanGenerate}");
    if (!vm.CanGenerate) { Fail("ensemble", "CanGenerate false"); return; }

    vm.GenerateCommand.Execute(null);
    vm.StartQueueCommand.Execute(null);   // Ensemble's AddToQueue does not auto-start
    await WaitIdle(vm, "ensemble", busy: v => v.IsProcessing || v.IsProcessingQueue,
        status: v => v.QueueStatus, minutes: 20);
    Report("ensemble", vm);
}

// ───────────────────────────────────────────────────────────────────────────────

static async Task RunResolver(IServiceProvider sp)
{
    // Exercises the Phase 3 missing-node resolver against the live server: repo enrichment
    // through ComfyUI-Manager's map, pack-presence detection, and the headless no-window
    // path (the dialog itself cannot show without a UI - the resolver must log and
    // return false rather than throw).
    var installer = sp.GetRequiredService<FlipPix.UI.Linux.Services.NodeInstallerService>();
    var resolver = sp.GetRequiredService<FlipPix.Core.Interfaces.IMissingNodeResolver>();

    var info = new FlipPix.Core.Models.MissingNodeInfo { ClassType = "ImpactWildcardProcessor" };
    var list = new List<FlipPix.Core.Models.MissingNodeInfo> { info };

    await installer.ResolveReposAsync(list, CancellationToken.None);
    Console.WriteLine($"[resolver] {info.ClassType}: repo={info.RepoUrl ?? "<none>"} pack={info.PackName ?? "<none>"} " +
                      $"presentLocally={installer.IsPackPresent(info)} git={installer.GitAvailable()} canLocal={installer.CanInstallLocally()}");

    var offered = await resolver.TryResolveAsync(list, CancellationToken.None);
    Console.WriteLine($"[resolver] TryResolveAsync -> {offered} (expected False: no window to show the dialog on)");
    Console.WriteLine(offered ? "[resolver] FAIL (dialog cannot show headless)" : "[resolver] PASS");
}

static async Task WaitIdle<T>(T vm, string label,
    Func<T, bool> busy,
    Func<T, string> status, int minutes)
{
    var sw = Stopwatch.StartNew();
    string last = "";
    while (sw.Elapsed < TimeSpan.FromMinutes(minutes))
    {
        await Task.Delay(2500);
        if (busy(vm)) continue;
        // one extra poll so queue-auto-start (fire-and-forget) is not mistaken for done
        await Task.Delay(1500);
        if (!busy(vm)) { Console.WriteLine($"[{label}] idle at +{sw.Elapsed.TotalSeconds:0}s"); return; }
        var s = status(vm);
        if (s != last) { Console.WriteLine($"[{label} +{sw.Elapsed.TotalSeconds:0}s] {s}"); last = s; }
    }
    Console.WriteLine($"[{label}] TIMEOUT after {minutes} min");
}

static void Report(string label, VideoProcessingBaseViewModel vm)
{
    Console.WriteLine($"[{label}] HasResult={vm.HasResult} ResultVideoPath={vm.ResultVideoPath}");
    var ok = vm.HasResult && !string.IsNullOrEmpty(vm.ResultVideoPath) && File.Exists(vm.ResultVideoPath);
    Console.WriteLine(ok
        ? $"[{label}] PASS  ({new FileInfo(vm.ResultVideoPath!).Length / 1024 / 1024.0:0.0} MB)"
        : $"[{label}] FAIL");
    if (!ok) TailLog();
}

static void Fail(string label, string why)
{
    Console.WriteLine($"[{label}] FAIL: {why}");
    TailLog();
}

static string WhichFfmpeg() =>
    Environment.GetEnvironmentVariable("FLIPPIX_FFMPEG") is { Length: > 0 } e ? e : "ffmpeg";

static void TailLog()
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/state/FlipPix/logs");
    var newest = new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
    if (newest == null) return;
    Console.WriteLine($"--- tail of {newest.Name} ---");
    Console.WriteLine(string.Join('\n', File.ReadAllLines(newest.FullName).TakeLast(25)));
}
