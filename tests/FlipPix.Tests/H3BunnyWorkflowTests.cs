using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace FlipPix.Tests;

/// <summary>
/// The contract between <c>workflow/video/h3-minimax/h3-bunny.json</c> and ⚡ H3 Express's 🐰 BUNNY stack.
/// The graph is generated from the author's export by <c>tools/build_h3_bunny.py</c>, so a re-export — or a
/// hand edit of the built file — can quietly move an id the render path writes to. Every one of those
/// writes is a <c>SetInput</c>, a <c>Link</c> or a <c>RequireClass</c> that throws at submit time, on a tab
/// that runs folders of films unattended overnight; these assertions are the same statements, made at build
/// time instead.
///
/// <para>This stack is the one that overrides <i>nothing</i> in the render path: the shared
/// <c>H3ErosViewModel.BuildFinish</c> writes the clip's seed into the branch's <c>RandomNoise</c> and
/// upscales whatever <c>125:12</c> produced. That only gives the author's render if <c>125:12</c> is the
/// <b>end</b> of the two-stage relay rather than the middle of it, which is what most of the assertions
/// below are about.</para>
///
/// <para>What the render path itself does with the graph — the panels injected per cast member, the LoRA
/// splice, the RIFE relink and the prune — needs a live ComfyUI to validate against and lives in
/// <c>tools/verify_h3_bunny.py</c>.</para>
/// </summary>
public sealed class H3BunnyWorkflowTests
{
    /// <summary>(stage-2 sampler, preview sink, noise) — H3ErosViewModel.SampleBranches — and the stage-1
    /// sampler this stack puts between the seed and the take.</summary>
    private static readonly (string Second, string Sink, string Noise, string First)[] Branches =
    {
        ("125:12", "18", "125:17", "bn:s1a"),
        ("133:129", "134", "133:128", "bn:s2a"),
        ("143:139", "144", "143:138", "bn:s3a"),
    };

    private static JsonObject Graph()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "workflow", "video", "h3-minimax", "h3-bunny.json");
        Assert.True(File.Exists(path), $"the BUNNY graph is not in the output: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static JsonObject Inputs(JsonObject root, string id)
    {
        Assert.True(root.ContainsKey(id), $"node {id} is not in the graph");
        return root[id]!["inputs"]!.AsObject();
    }

    private static string Class(JsonObject root, string id) =>
        root[id]!["class_type"]!.GetValue<string>();

    private static (string Node, int Slot) Link(JsonObject root, string id, string input)
    {
        var link = Inputs(root, id)[input]?.AsArray();
        Assert.True(link is { Count: 2 }, $"{id}.{input} is not a link");
        return (link![0]!.GetValue<string>(), link[1]!.GetValue<int>());
    }

    [Theory]
    // The ids H3ErosViewModel.ApplyCommonInputs writes to, shared by every stack.
    [InlineData("22:11", "PrimitiveStringMultiline")]
    [InlineData("22:23", "PrimitiveFloat")]
    [InlineData("22:8", "INTConstant")]
    [InlineData("22:9", "ResolutionSelector")]
    [InlineData("5", "MiniMaxH3ReferenceToVideo")]
    [InlineData("171:4", "UNETLoader")]
    [InlineData("21", "Power Lora Loader (rgthree)")]
    // The ids the shared latent-upscale finish writes to and relinks.
    [InlineData("242", "LTXVSeparateAVLatent")]
    [InlineData("243", "MinimaxH3LatentUpscaler3D")]
    [InlineData("135:26", "SamplerCustomAdvanced")]
    [InlineData("135:27", "RandomNoise")]
    [InlineData("259", "VAEDecode")]
    [InlineData("258", "VAEDecodeAudio")]
    [InlineData("189", "VAEDecode")]
    [InlineData("190", "VAEDecodeAudio")]
    [InlineData("165", "RIFEInterpolation")]
    [InlineData("34", "VHS_VideoCombine")]
    // The fixed schedules the ⬆ steps dial picks between.
    [InlineData("222", "ManualSigmas")]
    [InlineData("221", "ManualSigmas")]
    [InlineData("220", "ManualSigmas")]
    // BUNNY's own.
    [InlineData("bn:split", "SplitSigmasDenoise")]
    [InlineData("bn:ext", "ExtendIntermediateSigmas")]
    [InlineData("bn:nonoise", "DisableNoise")]
    public void Every_node_the_tab_drives_is_there_with_the_class_it_expects(string id, string expected)
    {
        var root = Graph();
        Assert.True(root.ContainsKey(id), $"node {id} ({expected}) is missing");
        Assert.Equal(expected, Class(root, id));
    }

    [Fact]
    public void Every_hunt_branch_carries_the_ids_the_shared_render_path_names()
    {
        var root = Graph();
        foreach (var (second, sink, noise, first) in Branches)
        {
            Assert.Equal("SamplerCustomAdvanced", Class(root, second));
            Assert.Equal("SamplerCustomAdvanced", Class(root, first));
            Assert.Equal("RandomNoise", Class(root, noise));
            Assert.Equal("VHS_VideoCombine", Class(root, sink));
        }
    }

    [Fact]
    public void One_schedule_is_built_once_extended_and_split_between_the_two_stages()
    {
        var root = Graph();

        // The step count the tab writes into 22:8 is the scheduler's, and the extension and the split are
        // both downstream of it — so moving the slider moves the whole relay, not one half of it.
        Assert.Equal("22:8", Link(root, "22:7", "steps").Node);
        Assert.Equal("22:7", Link(root, "bn:ext", "sigmas").Node);
        Assert.Equal("bn:ext", Link(root, "bn:split", "sigmas").Node);
        Assert.Equal(1.0, Inputs(root, "22:7")["denoise"]!.GetValue<double>());

        var split = Inputs(root, "bn:split")["denoise"]!.GetValue<double>();
        Assert.InRange(split, 0.01, 0.99);
    }

    [Fact]
    public void Each_branch_relays_one_seed_across_the_split_without_re_noising()
    {
        var root = Graph();
        foreach (var (second, _sink, noise, first) in Branches)
        {
            // Stage 1: the branch's own seed, the high sigmas, the reference latent.
            Assert.Equal(noise, Link(root, first, "noise").Node);
            Assert.Equal(("bn:split", 0), Link(root, first, "sigmas"));
            Assert.Equal("5", Link(root, first, "latent_image").Node);

            // Stage 2: no new noise, the low sigmas, and stage 1's latent continued.
            Assert.Equal("bn:nonoise", Link(root, second, "noise").Node);
            Assert.Equal(("bn:split", 1), Link(root, second, "sigmas"));
            Assert.Equal(first, Link(root, second, "latent_image").Node);
        }
    }

    [Fact]
    public void The_finish_upscales_the_end_of_the_relay_not_the_middle_of_it()
    {
        var root = Graph();
        var take = Branches[0].Second;

        // H3ErosViewModel.BuildFinish repoints these three at the picked branch's sampler and upscales
        // what it finds. Shipped pointing at stage 2, because a clip that is still a quarter un-denoised
        // being handed to the latent upscaler looks like a broken checkpoint, not like a mis-wiring.
        Assert.Equal(take, Link(root, "242", "av_latent").Node);
        Assert.Equal(take, Link(root, "259", "samples").Node);
        Assert.Equal(take, Link(root, "258", "samples").Node);

        Assert.Equal("242", Link(root, "243", "latent").Node);
        Assert.Equal("243", Link(root, "244", "video_latent").Node);
        Assert.Equal("244", Link(root, "135:26", "latent_image").Node);
        Assert.Equal("135:27", Link(root, "135:26", "noise").Node);
    }

    [Fact]
    public void The_only_seeds_in_the_graph_are_one_per_branch_plus_the_upscale_pass()
    {
        var root = Graph();
        var seeds = root.Where(n => n.Value!["class_type"]!.GetValue<string>() == "RandomNoise")
                        .Select(n => n.Key)
                        .OrderBy(k => k)
                        .ToList();
        var expected = Branches.Select(b => b.Noise).Append("135:27").OrderBy(k => k).ToList();
        Assert.Equal(expected, seeds);
    }

    [Fact]
    public void Both_stages_sample_through_the_power_lora_seat_the_tab_splices_into()
    {
        var root = Graph();

        // H3ExpressViewModel.ApplyCommonInputs retargets every reader of node 21 at its own LoRA node. The
        // seat therefore has to sit ABOVE the split: a cleanup stage sampling without the LoRA the stage
        // that decided the motion had would undo it.
        Assert.Equal("Power Lora Loader (rgthree)", Class(root, "21"));
        foreach (var guider in new[] { "bn:guide1", "bn:guide2", "135:29" })
            Assert.True(ReadsThrough(root, Link(root, guider, "model").Node, "21"),
                        $"{guider} does not sample through node 21");

        // …and so does the scheduler, which is what the sigmas are built from.
        Assert.True(ReadsThrough(root, Link(root, "22:7", "model").Node, "21"),
                    "the scheduler does not read the model wire the LoRA is on");
    }

    [Fact]
    public void The_two_stages_load_the_same_lora_at_the_authored_strengths()
    {
        var root = Graph();
        var strong = Inputs(root, "bn:combat1");
        var soft = Inputs(root, "bn:combat2");

        Assert.Equal(strong["lora_name"]!.GetValue<string>(), soft["lora_name"]!.GetValue<string>());
        Assert.True(strong["strength_model"]!.GetValue<double>() > soft["strength_model"]!.GetValue<double>(),
                    "stage 2 is the cleanup pass — it samples the Combat LoRA at the lower weight");

        Assert.Equal("bn:combat1", Link(root, "bn:guide1", "model").Node);
        Assert.Equal("bn:combat2", Link(root, "bn:guide2", "model").Node);
    }

    [Fact]
    public void The_shipped_checkpoint_is_one_the_reference_conditioning_was_built_for()
    {
        var root = Graph();
        var unet = Inputs(root, "171:4")["unet_name"]!.GetValue<string>().Replace('\\', '/');

        // Every clip on this tab conditions on cast panels wired into MiniMaxH3ReferenceToVideo, never on a
        // first or last frame — which is what the author's note prescribes the hybrid for, and what
        // H3ErosViewModel.DiffusionModelSummary warns about when the loaded model is neither.
        Assert.Contains("h3-minimax/", unet);
        Assert.True(unet.Contains("hybrid", StringComparison.OrdinalIgnoreCase)
                    || unet.Contains("ref2va", StringComparison.OrdinalIgnoreCase),
                    $"{unet} is not a reference-trained build");
    }

    /// <summary>Walks a MODEL wire back from <paramref name="from"/> looking for <paramref name="seat"/>.</summary>
    private static bool ReadsThrough(JsonObject root, string? from, string seat)
    {
        var seen = new HashSet<string>();
        while (from != null && seen.Add(from))
        {
            if (from == seat) return true;
            if (!root.ContainsKey(from)) return false;
            from = root[from]!["inputs"]?["model"]?.AsArray() is { Count: 2 } link
                ? link[0]!.GetValue<string>()
                : null;
        }
        return false;
    }
}
