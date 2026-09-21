using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.ComfyUI.Http;

namespace FlipPix.Tests;

/// <summary>
/// The pre-submit repair that keeps a ComfyUI update from silently killing every render
/// (<see cref="ComfyUIHttpClient.FillMissingRequiredInputs"/>).
///
/// <para>The failure it exists for: a custom-node pack adds a widget, declares it required, and every
/// graph exported before that update answers <c>required_input_missing</c> — which drops the node and
/// its whole upstream chain, so the submission "succeeds" with no output. It happened to
/// <c>MinimaxH3LatentUpscaler3D</c> (new <c>enable_temporal_chunking</c> / <c>force_unload</c>) and took
/// out every H3 tab at once, because they all sample draft → latent upscale → finish.</para>
///
/// <para>What is worth pinning here is the judgement in the pass rather than the plumbing: it fills a
/// widget the server gives a default for, it does <b>not</b> invent a link input, and it does not guess
/// at a combo listing the server's own files — a <c>LoadImage</c> quietly pointed at whatever picture
/// sorts first would be worse than the validation error it replaced.</para>
/// </summary>
public sealed class MissingRequiredInputsTests
{
    /// <summary>
    /// The real /object_info shape for the node that broke, trimmed to what the pass reads: two new
    /// BOOLEAN widgets with defaults, a "*" link input, a COMBO of the server's own model files with no
    /// default, and a dynamic combo whose sub-inputs depend on the option the graph picked.
    /// </summary>
    private const string UpscalerSchema = """
    {
      "MinimaxH3LatentUpscaler3D": {
        "input": {
          "required": {
            "latent": ["*", {}],
            "model_name": ["COMBO", { "options": ["a.safetensors", "b.safetensors"] }],
            "mode": ["COMFY_DYNAMICCOMBO_V3", { "options": [
              { "key": "scale by multiplier", "inputs": { "required": {
                  "scale": ["FLOAT", { "default": 2.0, "min": 1.0, "max": 4.0 }] } } },
              { "key": "target dimensions", "inputs": { "required": {
                  "width": ["INT", { "default": 1280 }],
                  "height": ["INT", { "default": 704 }] } } }
            ] }],
            "align": ["INT", { "default": 32 }],
            "enable_temporal_chunking": ["BOOLEAN", { "default": true }],
            "force_unload": ["BOOLEAN", { "default": true }],
            "precision": ["COMBO", { "default": "fp16", "options": ["fp32", "fp16", "bf16"] }]
          }
        }
      }
    }
    """;

    /// <summary>The upscaler as every H3 graph held it before the update: no chunking, no unload.</summary>
    private static JsonObject StaleGraph() => JsonNode.Parse("""
    {
      "243": {
        "inputs": {
          "model_name": "a.safetensors",
          "mode": "scale by multiplier",
          "mode.scale": 2.0,
          "align": 32,
          "precision": "fp16",
          "latent": ["242", 0]
        },
        "class_type": "MinimaxH3LatentUpscaler3D"
      }
    }
    """)!.AsObject();

    private static JsonObject Inputs(JsonObject graph, string id) => graph[id]!["inputs"]!.AsObject();

    [Fact]
    public void FillsTheWidgetsThePackAdded()
    {
        var graph = StaleGraph();
        var fills = ComfyUIHttpClient.FillMissingRequiredInputs(graph, UpscalerSchema);

        var inputs = Inputs(graph, "243");
        Assert.True(inputs["enable_temporal_chunking"]!.GetValue<bool>());
        Assert.True(inputs["force_unload"]!.GetValue<bool>());
        Assert.Equal(2, fills.Count);
    }

    [Fact]
    public void LeavesAGraphThatAlreadySetsThemAlone()
    {
        var graph = StaleGraph();
        var inputs = Inputs(graph, "243");
        inputs["enable_temporal_chunking"] = false;
        inputs["force_unload"] = false;

        var fills = ComfyUIHttpClient.FillMissingRequiredInputs(graph, UpscalerSchema);

        Assert.Empty(fills);
        Assert.False(Inputs(graph, "243")["enable_temporal_chunking"]!.GetValue<bool>());
        Assert.False(Inputs(graph, "243")["force_unload"]!.GetValue<bool>());
    }

    /// <summary>
    /// A missing MODEL/LATENT/"*" input is a wiring bug in the graph FlipPix built, not version skew.
    /// Inventing a value there would turn a clear validation error into a render of the wrong thing.
    /// </summary>
    [Fact]
    public void NeverInventsALinkInput()
    {
        var graph = StaleGraph();
        Inputs(graph, "243").Remove("latent");

        ComfyUIHttpClient.FillMissingRequiredInputs(graph, UpscalerSchema);

        Assert.False(Inputs(graph, "243").ContainsKey("latent"));
    }

    /// <summary>
    /// A combo with no declared default is usually a list of the server's own files. The front-end would
    /// take the first one; this pass will not, because the wrong checkpoint renders silently.
    /// </summary>
    [Fact]
    public void DoesNotGuessAtAComboWithNoDefault()
    {
        var graph = StaleGraph();
        Inputs(graph, "243").Remove("model_name");

        ComfyUIHttpClient.FillMissingRequiredInputs(graph, UpscalerSchema);

        Assert.False(Inputs(graph, "243").ContainsKey("model_name"));
    }

    /// <summary>A combo the server <i>does</i> give a default for is filled, like any other widget.</summary>
    [Fact]
    public void FillsAComboThatHasADefault()
    {
        var graph = StaleGraph();
        Inputs(graph, "243").Remove("precision");

        ComfyUIHttpClient.FillMissingRequiredInputs(graph, UpscalerSchema);

        Assert.Equal("fp16", Inputs(graph, "243")["precision"]!.GetValue<string>());
    }

    /// <summary>
    /// A dynamic combo's sub-inputs are flat keys ("mode.scale") and which ones are required depends on
    /// the option the graph is set to — so only the chosen branch may be filled.
    /// </summary>
    [Theory]
    [InlineData("scale by multiplier", "mode.scale", "mode.width")]
    [InlineData("target dimensions", "mode.width", "mode.scale")]
    public void FillsOnlyTheChosenDynamicComboBranch(string mode, string wanted, string unwanted)
    {
        var graph = StaleGraph();
        var inputs = Inputs(graph, "243");
        inputs["mode"] = mode;
        inputs.Remove("mode.scale");

        ComfyUIHttpClient.FillMissingRequiredInputs(graph, UpscalerSchema);

        Assert.True(Inputs(graph, "243").ContainsKey(wanted));
        Assert.False(Inputs(graph, "243").ContainsKey(unwanted));
    }

    /// <summary>A class the connected ComfyUI has never heard of is left for the missing-node resolver.</summary>
    [Fact]
    public void IgnoresAClassTheServerDoesNotHave()
    {
        var graph = StaleGraph();
        graph["243"]!["class_type"] = "SomeNodeThisServerLacks";

        Assert.Empty(ComfyUIHttpClient.FillMissingRequiredInputs(graph, UpscalerSchema));
    }

    /// <summary>
    /// The shipped graphs carry the two inputs themselves, so a run does not depend on the repair pass
    /// having reached /object_info. A re-export of any of these files drops them again — that is the
    /// edit this assertion is here to catch.
    /// </summary>
    [Theory]
    [InlineData("h3-minimax-i2v.json")]
    [InlineData("h3-eros.json")]
    [InlineData("h3-singularity.json")]
    [InlineData("h3-bunny.json")]
    [InlineData("h3-cast-hybrid.json")]
    [InlineData("h3-minimax-fflf.json")]
    [InlineData("h3-seed-upscale.json")]
    [InlineData("h3-multi.json")]
    [InlineData("h3-duo.json")]
    [InlineData("h3-experimental.json")]
    [InlineData("h3-hd-detailer.json")]
    [InlineData("h3facerefiner.json")]
    public void TheShippedGraphsSetThemAlready(string file)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "workflow", "video", "h3-minimax", file);
        Assert.True(File.Exists(path), $"{file} is not in the output: {path}");

        var graph = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var upscalers = graph
            .Where(n => n.Value?["class_type"]?.GetValue<string>() == "MinimaxH3LatentUpscaler3D")
            .ToList();
        Assert.NotEmpty(upscalers);

        foreach (var (id, node) in upscalers)
        {
            var inputs = node!["inputs"]!.AsObject();
            Assert.True(inputs.ContainsKey("enable_temporal_chunking"),
                $"{file} node {id} is missing enable_temporal_chunking");
            Assert.True(inputs.ContainsKey("force_unload"),
                $"{file} node {id} is missing force_unload");
            // Dropped by the same pack update that added the two above.
            Assert.False(inputs.ContainsKey("keep_proportion"),
                $"{file} node {id} still carries keep_proportion, which the node no longer has");
        }
    }
}
