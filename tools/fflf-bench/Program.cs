// Development-only: builds one ⛓ FFLF beat's API graph with the app's own FflfGraph.Build, so the
// benchmark (bench.py) submits exactly what the tab would for a given model / text encoder / LoRA mix.
//
// Usage: fflf-bench <request.json> <out-graph.json>
// request.json: { template, beat, seconds, style, setting, wardrobe, description, sheetLabels[],
//                 isFirstBeat, render: { …FflfBeatRender fields… } }

using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlipPix.UI.Services;
using FlipPix.Core.Models;

// fflf-bench sheet <request.json> <out-graph.json> — one 🧾 character sheet, built by FflfSheetGraph exactly as the
// tab builds it. request.json: { engine, repo (the repository root, for the workflow files), photo, description, letter, seed, prefix }
if (args[0] == "sheet")
{
    var q = JsonNode.Parse(File.ReadAllText(args[1]))!.AsObject();
    string Q(string k) => q[k]?.GetValue<string>() ?? string.Empty;
    var cast = FflfCast.Parse(Q("description"));
    var member = cast.First(c => c.Letter == Q("letter"));
    var sheetPrompt = FflfSheetGraph.Prompt(member, cast.Count);
    var wf = FflfSheetGraph.WorkflowFile(Q("engine")) is { } wfFile ? File.ReadAllText(Path.Combine(RepoRoot(Q("repo")), wfFile)) : null;
    var g = FflfSheetGraph.Build(Q("engine"), wf, Q("photo"), sheetPrompt, q["seed"]!.GetValue<long>(), Q("prefix"),
                              referenceResolution: q["resolution"]?.GetValue<int>());
    File.WriteAllText(args[2], g.ToJsonString());
    Console.WriteLine(JsonSerializer.Serialize(new { saveNode = FflfSheetGraph.SaveNode(Q("engine")), prompt = sheetPrompt }));
    return;
}

// fflf-bench martial <request.json> <out-graph.json> — one 🥷 Martial beat, built by MartialGraph exactly as the tab
// builds it. request.json: { beat, seconds, style, setting, wardrobe, triggers, roles[], render: { pictures[], firstFrame,
// seed, aspectRatio, firstPassMegapixels, finalMegapixels, steps, diffusionModel, refineModel, textEncoder,
// loras[{name,strength,onRefine}], refineStrength, refineSteps, maxFidelityReferences, outputPrefix } }
if (args[0] == "martial")
{
    var q = JsonNode.Parse(File.ReadAllText(args[1]))!.AsObject();
    string Q(string k) => q[k]?.GetValue<string>() ?? string.Empty;
    var m = q["render"]!.AsObject();
    string M(string k, string d = "") => m[k]?.GetValue<string>() ?? d;
    double MD(string k, double d) => m[k]?.GetValue<double>() ?? d;
    var roles = q["roles"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
    var look = FflfGraph.ComposeLook(Q("style"), Q("setting"), Q("wardrobe"));
    var martialPrompt = MartialGraph.ComposePrompt(Q("triggers"), Q("beat"), look, roles);
    var rows = (m["loras"]?.AsArray() ?? new JsonArray()).Select(n => (
        Name: n!["name"]!.GetValue<string>(), Strength: n["strength"]!.GetValue<double>(),
        OnRefine: n["onRefine"]?.GetValue<bool>() ?? true)).ToList();
    var beatRender = new MartialBeatRender
    {
        Pictures = m["pictures"]!.AsArray().Select(n => n!.GetValue<string>()).ToList(),
        FirstFrame = m["firstFrame"]?.GetValue<string>(),
        Prompt = martialPrompt,
        Seconds = q["seconds"]!.GetValue<double>(),
        Seed = m["seed"]!.GetValue<long>(),
        AspectRatio = M("aspectRatio", "16:9 (Widescreen)"),
        FirstPassMegapixels = MD("firstPassMegapixels", 0.8),
        FinalMegapixels = MD("finalMegapixels", 1.2),
        Steps = (int)MD("steps", 8),
        DiffusionModel = M("diffusionModel"),
        RefineModel = M("refineModel"),
        TextEncoder = M("textEncoder"),
        Loras = rows.Select(l => new MartialLora(l.Name, l.Strength)).ToList(),
        RefineLoras = rows.Where(l => l.OnRefine).Select(l => new MartialLora(l.Name, l.Strength)).ToList(),
        MaxFidelityReferences = m["maxFidelityReferences"]?.GetValue<bool>() ?? false,
        RefineStrength = MD("refineStrength", 0.6),
        RefineSteps = (int)MD("refineSteps", 10),
        OutputPrefix = M("outputPrefix", "video/fflf-bench/martial/beat"),
    };
    File.WriteAllText(args[2], MartialGraph.Build(beatRender).ToJsonString());
    Console.WriteLine(JsonSerializer.Serialize(new { video = MartialGraph.VideoSaveNode, last = MartialGraph.LastFrameSaveNode, chars = martialPrompt.Length }));
    return;
}

var req = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
string S(string k) => req[k]?.GetValue<string>() ?? string.Empty;
var r = req["render"]!.AsObject();
string R(string k, string d = "") => r[k]?.GetValue<string>() ?? d;
double D(string k, double d) => r[k]?.GetValue<double>() ?? d;

var sheets = req["sheetLabels"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? new List<string>();
var identity = r["identityImage"]?.GetValue<string>();
var people = FflfCast.Parse(S("description"));
var seconds = req["seconds"]!.GetValue<double>();
var prompt = FflfGraph.ComposeH3Prompt(S("beat"), seconds, S("style"), S("setting"), FflfGraph.H3Cast(people, S("wardrobe")),
                                       identity != null, req["isFirstBeat"]!.GetValue<bool>(), sheets);

// Trigger words in front (the 🥷 Martial tab's "wushu_action, camera motion," — see MartialGraph.ComposePrompt).
var prefix = S("promptPrefix").Trim().TrimEnd(',', ' ');
if (prefix.Length > 0) prompt = prefix + ",\n" + prompt;

var extras =(r["extraLoras"]?.AsArray() ?? new JsonArray())
    .Select(n => new MiniMaxI2VLora { Name = n!["name"]!.GetValue<string>(), Strength = n["strength"]!.GetValue<double>() })
    .ToList();

var render = new FflfBeatRender
{
    StartImage = R("startImage"),
    IdentityImage = identity,
    CastImages = r["castImages"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? new List<string>(),
    Prompt = prompt,
    Seconds = seconds,
    Seed = r["seed"]!.GetValue<long>(),
    AspectRatio = R("aspectRatio", "16:9 (Widescreen)"),
    FirstPassMegapixels = D("firstPassMegapixels", 0.3),
    FinalMegapixels = D("finalMegapixels", 1.0),
    FirstPassSteps = (int)D("firstPassSteps", 8),
    PreviewOnly = r["previewOnly"]?.GetValue<bool>() ?? false,
    DiffusionModel = R("diffusionModel"),
    TextEncoder = R("textEncoder"),
    Lora = R("lora"),
    LoraStrength = D("loraStrength", 0),
    ExtraLoras = extras,
    // The 🥋 Action tab's softer refine-pass mix; absent = the refine runs on the first-pass mix (FFLF).
    RefineLoras = r["refineLoras"] is JsonArray refine
        ? refine.Select(n => new MiniMaxI2VLora { Name = n!["name"]!.GetValue<string>(), Strength = n["strength"]!.GetValue<double>() }).ToList()
        : null,
    LockFirstFrame = r["lockFirstFrame"]?.GetValue<bool>() ?? true,
    MaxFidelityReferences = r["maxFidelityReferences"]?.GetValue<bool>() ?? true,
    OutputPrefix = R("outputPrefix", "video/fflf-bench/beat"),
};

var graph = FflfGraph.Build(File.ReadAllText(S("template")), render, out var pruned);
File.WriteAllText(args[1], graph.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
Console.WriteLine($"ok pruned={pruned} prompt_chars={prompt.Length}");

// The repository root: the request's "repo", or the first folder above the exe that holds workflow/.
static string RepoRoot(string given)
{
    if (given.Length > 0) return given;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "workflow", "image"))) return d.FullName;
    throw new DirectoryNotFoundException("workflow/ not found above the exe.");
}
