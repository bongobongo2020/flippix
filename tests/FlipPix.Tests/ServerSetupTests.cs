using System.Text.Json;
using System.Text.Json.Nodes;
using FlipPix.ComfyUI.Setup;

namespace FlipPix.Tests;

public class ServerSetupTests
{
    // A tiny /object_info: a loader with a model combo, an output node, and a sampler-ish enum.
    private const string ObjectInfo = """
    {
      "LoraLoader": { "input": { "required": {
          "model": ["MODEL"],
          "lora_name": [["klein/klein_9B_Turbo_r128.safetensors", "LTX-23/ltx-2.3-22b-distilled-lora-384.safetensors", "other.safetensors"], {}],
          "strength_model": ["FLOAT", {"default": 1.0}] } }, "output_node": false },
      "UNETLoader": { "input": { "required": {
          "unet_name": ["COMBO", { "options": ["z_image_turbo_bf16.safetensors"] }],
          "weight_dtype": [["default", "fp8_e4m3fn"], {}] } }, "output_node": false },
      "KSampler": { "input": { "required": { "model": ["MODEL"], "sampler_name": [["euler", "dpmpp_2m"], {}] } }, "output_node": false },
      "SaveImage": { "input": { "required": { "images": ["IMAGE"] } }, "output_node": true }
    }
    """;

    private static JsonElement Oi() => JsonDocument.Parse(ObjectInfo).RootElement.Clone();

    private static WorkflowSource Wf(string name, string json) => new(name, JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Api_workflow_reports_missing_nodes_models_and_server_copies()
    {
        var wf = Wf("a.json", """
        {
          "1": { "class_type": "UNETLoader", "inputs": { "unet_name": "missing_unet.safetensors", "weight_dtype": "default" } },
          "2": { "class_type": "LoraLoader", "inputs": { "model": ["1", 0], "lora_name": "klein\\klein_9B_Turbo_r128.safetensors" } },
          "3": { "class_type": "LoraLoader", "inputs": { "model": ["2", 0], "lora_name": "ltx2/ltx-2.3-22b-distilled-lora-384.safetensors" } },
          "4": { "class_type": "FancyCustomNode", "inputs": { "model": ["3", 0] } },
          "5": { "class_type": "KSampler", "inputs": { "model": ["4", 0], "sampler_name": "euler" } },
          "6": { "class_type": "SaveImage", "inputs": { "images": ["5", 0] } },
          "9": { "class_type": "UNETLoader", "inputs": { "unet_name": "orphan_not_run.safetensors" } },
          "10": { "class_type": "Note", "inputs": {} }
        }
        """);

        var r = WorkflowRequirementScanner.Analyze(new[] { wf }, Oi(), '/');

        Assert.Equal(new[] { "FancyCustomNode" }, r.MissingNodes.Select(n => n.ClassType));
        // klein\ -> klein/ on a Linux host is a plain match, not a miss.
        var models = r.MissingModels.ToDictionary(m => m.Name);
        Assert.Equal(2, models.Count);
        Assert.Null(models["missing_unet.safetensors"].ServerMatch);
        Assert.Equal("diffusion_models", models["missing_unet.safetensors"].Category);
        Assert.Equal("LTX-23/ltx-2.3-22b-distilled-lora-384.safetensors", models["ltx2/ltx-2.3-22b-distilled-lora-384.safetensors"].ServerMatch);
        Assert.DoesNotContain("orphan_not_run.safetensors", models.Keys);

        var w = Assert.Single(r.Workflows);
        Assert.Equal(1, w.MissingNodes);
        Assert.Equal(1, w.MissingModels);
        Assert.Equal(1, w.RemappedModels);
        Assert.False(w.Ready);
    }

    [Fact]
    public void Missing_output_node_still_counts_its_upstream_graph()
    {
        // "Image Saver" is an output-ish custom node the server lacks; the loader feeding it must still be checked.
        var wf = Wf("b.json", """
        {
          "1": { "class_type": "UNETLoader", "inputs": { "unet_name": "nope.safetensors" } },
          "2": { "class_type": "Image Saver", "inputs": { "model": ["1", 0], "ckpt": "x.safetensors" } }
        }
        """);
        var r = WorkflowRequirementScanner.Analyze(new[] { wf }, Oi(), '/');
        Assert.Contains(r.MissingNodes, n => n.ClassType == "Image Saver");
        Assert.Contains(r.MissingModels, m => m.Name == "nope.safetensors");
        Assert.True(r.HasUncheckedModels);
    }

    [Fact]
    public void Ui_workflow_checks_widget_values_and_skips_muted_and_frontend_nodes()
    {
        var wf = Wf("ui.json", """
        {
          "nodes": [
            { "id": 1, "type": "LoraLoader", "mode": 0, "widgets_values": ["other.safetensors", 1.0] },
            { "id": 2, "type": "LoraLoader", "mode": 0, "widgets_values": ["not_there.safetensors", 1.0] },
            { "id": 3, "type": "MutedPack", "mode": 2, "widgets_values": [] },
            { "id": 4, "type": "Reroute", "mode": 0 },
            { "id": 5, "type": "UsedPack", "mode": 0, "widgets_values": [] },
            { "id": 6, "type": "3f2a6b0e-1111-2222-3333-444455556666", "mode": 0 }
          ],
          "definitions": { "subgraphs": [ { "nodes": [ { "id": 7, "type": "InsideSubgraph", "mode": 0 } ] } ] }
        }
        """);
        var r = WorkflowRequirementScanner.Analyze(new[] { wf }, Oi(), '/');
        Assert.Equal(new[] { "InsideSubgraph", "UsedPack" }, r.MissingNodes.Select(n => n.ClassType).OrderBy(x => x));
        var m = Assert.Single(r.MissingModels);
        Assert.Equal("not_there.safetensors", m.Name);
        Assert.Equal("loras", m.Category);
    }

    [Theory]
    [InlineData("a/x.safetensors", new[] { "b/x.safetensors" }, "b/x.safetensors")]
    [InlineData("a/X.safetensors", new[] { "b/x.safetensors" }, "b/x.safetensors")]          // case-only
    [InlineData("x.safetensors", new[] { "x.safetensors" }, null)]                            // already listed
    [InlineData("x.safetensors", new[] { "y.safetensors" }, null)]                            // not there
    [InlineData("wan/lora/x.safetensors", new[] { "old/x.safetensors", "WAN/lora/x.safetensors" }, "WAN/lora/x.safetensors")]
    public void FindServerMatch_picks_the_servers_copy(string wanted, string[] options, string? expected)
    {
        Assert.Equal(expected, WorkflowRequirementScanner.FindServerMatch(wanted, options));
    }

    [Fact]
    public void RemapModelsToServerCopies_rewrites_only_model_inputs_it_can_place()
    {
        var prompt = (JsonObject)JsonNode.Parse("""
        {
          "1": { "class_type": "LoraLoader", "inputs": { "lora_name": "ltx2/ltx-2.3-22b-distilled-lora-384.safetensors" } },
          "2": { "class_type": "LoraLoader", "inputs": { "lora_name": "really_missing.safetensors" } },
          "3": { "class_type": "LoraLoader", "inputs": { "lora_name": "other.safetensors" } }
        }
        """)!;
        var changes = WorkflowRequirementScanner.RemapModelsToServerCopies(prompt, Oi());
        Assert.Single(changes);
        Assert.Equal("LTX-23/ltx-2.3-22b-distilled-lora-384.safetensors", (string?)prompt["1"]!["inputs"]!["lora_name"]);
        Assert.Equal("really_missing.safetensors", (string?)prompt["2"]!["inputs"]!["lora_name"]);
        Assert.Equal("other.safetensors", (string?)prompt["3"]!["inputs"]!["lora_name"]);
    }

    [Fact]
    public void ShareGuesses_pair_each_segment_with_its_server_prefix()
    {
        var g = ModelShareLocator.ShareGuesses("/mnt/storage1/ai-models/loras").ToList();
        Assert.Contains(("ai-models", "/mnt/storage1/ai-models"), g);
        Assert.Contains(("loras", "/mnt/storage1/ai-models/loras"), g);
        var w = ModelShareLocator.ShareGuesses(@"D:\ComfyUI\models\vae").ToList();
        Assert.Contains(("models", "D:/ComfyUI/models"), w);
    }

    [Fact]
    public void MapCandidates_translate_server_paths_under_a_known_root()
    {
        var roots = new[] { ("/mnt/storage1/ai-models", @"Z:\") };
        Assert.Equal(new[] { @"Z:\loras\klein" }, ModelShareLocator.MapCandidates("/mnt/storage1/ai-models/loras/klein", roots));
        Assert.Empty(ModelShareLocator.MapCandidates("/home/x2/ComfyUI/models/loras", roots));
        Assert.Empty(ModelShareLocator.MapCandidates("/mnt/storage1/ai-models-old/loras", roots));
    }

    [Fact]
    public void PrefixesEndingWith_only_names_the_folders_a_share_can_be()
    {
        var server = new[]
        {
            "/home/x2/jun1/ComfyUI/models/checkpoints",
            "/home/x2/sep23/ComfyUI/models/checkpoints",
            "/mnt/storage1/ai-models/checkpoints",
            "/mnt/storage1/ai-models/output/checkpoints",
        };
        Assert.Equal(new[] { "/mnt/storage1/ai-models" }, ModelShareLocator.PrefixesEndingWith(new[] { "ai-models" }, server));
        Assert.Equal(2, ModelShareLocator.PrefixesEndingWith(new[] { "models" }, server).Count); // ambiguous
        Assert.Equal(new[] { "/mnt/storage1/ai-models/output" },
            ModelShareLocator.PrefixesEndingWith(new[] { "ai-models", "output" }, server));
    }

    [Fact]
    public void FindPack_accepts_a_registry_id_or_a_repo_url()
    {
        var packs = new[]
        {
            new ManagerPack { Id = "comfyui-image-saver", Repository = "https://github.com/alexopus/ComfyUI-Image-Saver" },
        };
        Assert.NotNull(ComfyServerSetupClient.FindPack(packs, "comfyui-image-saver"));
        Assert.NotNull(ComfyServerSetupClient.FindPack(packs, "https://github.com/alexopus/ComfyUI-Image-Saver.git"));
        Assert.NotNull(ComfyServerSetupClient.FindPack(packs, "ComfyUI-Image-Saver"));
        Assert.Null(ComfyServerSetupClient.FindPack(packs, "something-else"));
    }

    [Fact]
    public void FindPackByRepo_matches_url_variants()
    {
        var packs = new[]
        {
            new ManagerPack { Id = "comfyui-kjnodes", Repository = "https://github.com/kijai/ComfyUI-KJNodes", Files = { "https://github.com/kijai/ComfyUI-KJNodes" } },
            new ManagerPack { Id = "other", Files = { "https://github.com/a/b.git" } },
        };
        Assert.Equal("comfyui-kjnodes", ComfyServerSetupClient.FindPackByRepo(packs, "https://github.com/kijai/comfyui-kjnodes/")?.Id);
        Assert.Equal("other", ComfyServerSetupClient.FindPackByRepo(packs, "http://github.com/a/b")?.Id);
        Assert.Null(ComfyServerSetupClient.FindPackByRepo(packs, "https://github.com/x/y"));
    }

    [Fact]
    public void Server_script_quotes_paths_and_resumes_downloads()
    {
        var bash = ServerSetupScript.Build(false, "http://srv:8188", "/opt/comfy/custom_nodes",
            new[] { "https://github.com/kijai/ComfyUI-KJNodes" },
            new[] { new ScriptModel("/mnt/models/loras", "klein/it's.safetensors", "https://h/f") });
        Assert.Contains("git clone --depth 1 'https://github.com/kijai/ComfyUI-KJNodes' 'ComfyUI-KJNodes'", bash);
        Assert.Contains("curl -L --fail -C - -o '/mnt/models/loras/klein/it'\\''s.safetensors' 'https://h/f'", bash);
        Assert.Contains("mkdir -p '/mnt/models/loras/klein'", bash);

        var ps = ServerSetupScript.Build(true, "http://srv:8188", @"C:\Comfy\custom_nodes", Array.Empty<string>(),
            new[] { new ScriptModel(@"C:\Comfy\models\vae", "ae.safetensors", "https://h/ae") });
        Assert.Contains(@"curl.exe -L --fail -C - -o 'C:\Comfy\models\vae\ae.safetensors' 'https://h/ae'", ps);
    }
}
