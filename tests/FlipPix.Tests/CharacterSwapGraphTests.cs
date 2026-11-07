using System.IO;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// The 🔁 Character Swap tab prunes Openjutsu's API exports per request (<see cref="CharacterSwapGraph"/>,
/// a port of its lib/simple.ts, lib/advanced.ts and lib/model.ts). These are the same checks upstream's
/// <c>npm test</c> makes against the exports: every option combination leaves a graph with no link to a
/// removed node, and the values the tab sets land where H3 reads them.
/// </summary>
public sealed class CharacterSwapGraphTests
{
    private static JsonObject Load(string file)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "workflow", "video", "h3-minimax", "character-swap", file);
        Assert.True(File.Exists(path), $"the Character Swap graph is not in the output: {path}");
        var graph = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        CharacterSwapGraph.SilenceWhenSoundless(graph);
        return graph;
    }

    private static string Class(JsonObject g, string id) => g[id]!["class_type"]!.GetValue<string>();

    public static TheoryData<bool, bool, int> Options()
    {
        var data = new TheoryData<bool, bool, int>();
        foreach (var hat in new[] { false, true })
            foreach (var lip in new[] { false, true })
                foreach (var motion in CharacterSwapGraph.MotionSizes)
                    data.Add(hat, lip, motion);
        return data;
    }

    [Theory]
    [MemberData(nameof(Options))]
    public void Simple_prunes_cleanly_for_every_option(bool hatFix, bool noLipSync, int motion)
    {
        var s = new SwapSettings { HatFix = hatFix, NoLipSync = noLipSync, MotionSize = motion, Roles = { "  the   singer " }, Seed = 42, Resolution = "480p" };
        var g = CharacterSwapGraph.PruneSimple(Load(CharacterSwapGraph.SimpleFile), s);
        CharacterSwapGraph.ApplyModel(g, new SwapModel());
        CharacterSwapGraph.AssertNoDanglingLinks(g);

        var plan = g[CharacterSwapGraph.SimpleNodes.Plan]!["inputs"]!;
        Assert.Equal("the singer", plan["roles"]!.GetValue<string>());
        Assert.Equal(42, plan["seed"]!.GetValue<long>());
        Assert.Equal("480p", plan["resolution"]!.GetValue<string>());
        Assert.Equal(hatFix, g.ContainsKey(CharacterSwapGraph.SimpleNodes.Hat[0]));
        Assert.Equal(noLipSync, g.Any(kv => CharacterSwapGraph.TitleOf(kv.Value) == "No lip-sync in the prompt"));
        Assert.Equal(motion != 512, g.ContainsKey("motion_run_name"));
        Assert.Equal("MiniMaxH3ChainAssemble", Class(g, CharacterSwapGraph.NodeByTitle(g, CharacterSwapGraph.FinalVideoTitle)));
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, true, true)]
    [InlineData(3, false, true)]
    [InlineData(4, true, false)]
    public void Advanced_keeps_only_the_pictures_it_uses(int pictures, bool hatFix, bool noLipSync)
    {
        var s = new SwapSettings { HatFix = hatFix, NoLipSync = noLipSync, Roles = { "the drummer", "" , "the bassist", "" } };
        var g = CharacterSwapGraph.PruneAdvanced(Load(CharacterSwapGraph.AdvancedFile), pictures, s);
        CharacterSwapGraph.ApplyModel(g, new SwapModel());
        CharacterSwapGraph.AssertNoDanglingLinks(g);

        Assert.Equal(pictures, CharacterSwapGraph.PictureCount(g));
        var roles = g[CharacterSwapGraph.NodeByTitle(g, CharacterSwapGraph.AdvancedPlanTitle)]!["inputs"]!["roles"]!.GetValue<string>();
        Assert.Equal(pictures, roles.Split('\n').Length);
        Assert.StartsWith("the drummer", roles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mask_check_saves_its_preview(bool hatFix)
    {
        var g = CharacterSwapGraph.PruneMask(Load(CharacterSwapGraph.MaskFile), hatFix, SwapMargin.Loose, "cat");
        CharacterSwapGraph.AssertNoDanglingLinks(g);
        Assert.Equal("cat", g[CharacterSwapGraph.MaskNodes.Target]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal(60, g[CharacterSwapGraph.MaskNodes.GrowMask]!["inputs"]!["expand"]!.GetValue<int>());
        Assert.True(g[CharacterSwapGraph.MaskNodes.Preview]!["inputs"]!["save_output"]!.GetValue<bool>());
    }

    [Fact]
    public void Lora_chain_replaces_the_DMAD_node_or_links_straight_through()
    {
        var none = CharacterSwapGraph.PruneSimple(Load(CharacterSwapGraph.SimpleFile), new SwapSettings());
        CharacterSwapGraph.ApplyModel(none, new SwapModel { Loras = new(), Steps = 20 });
        CharacterSwapGraph.AssertNoDanglingLinks(none);
        Assert.DoesNotContain(none, kv => Class(none, kv.Key) == "LoraLoaderModelOnly");

        var two = CharacterSwapGraph.PruneSimple(Load(CharacterSwapGraph.SimpleFile), new SwapSettings());
        CharacterSwapGraph.ApplyModel(two, new SwapModel { Loras = new() { new("a.safetensors", 1), new("b.safetensors", 0.5) } });
        CharacterSwapGraph.AssertNoDanglingLinks(two);
        Assert.Equal(2, two.Count(kv => Class(two, kv.Key) == "LoraLoaderModelOnly"));
        Assert.True(two.ContainsKey("model_run_name"));
    }

    [Fact]
    public void Soundless_videos_read_silence()
    {
        var g = Load(CharacterSwapGraph.SimpleFile);
        var video = CharacterSwapGraph.SimpleNodes.Video;
        Assert.Equal("H3SoundtrackOrSilence", Class(g, $"{video}_soundtrack"));
        Assert.Equal("AudioConcat", Class(g, $"{video}_soundtrack_padded"));
        // Nothing reads the raw audio output any more; everything reads the padded soundtrack.
        Assert.DoesNotContain(g, kv => kv.Key != $"{video}_soundtrack"
            && kv.Value!["inputs"]!.AsObject().Any(i => CharacterSwapGraph.IsLink(i.Value, out var src, out var slot) && src == video && slot == 2));
        CharacterSwapGraph.AssertNoDanglingLinks(g);
    }

    [Theory]
    [InlineData(CharacterSwapGraph.SimpleFile)]
    [InlineData(CharacterSwapGraph.AdvancedFile)]
    [InlineData(CharacterSwapGraph.MaskFile)]
    public void Sources_without_audio_never_link_the_loaders_audio_output(string file)
    {
        var g = Load(file);
        CharacterSwapGraph.UseSilenceForSoundless(g);
        g = file switch
        {
            CharacterSwapGraph.SimpleFile => CharacterSwapGraph.PruneSimple(g, new SwapSettings()),
            CharacterSwapGraph.AdvancedFile => CharacterSwapGraph.PruneAdvanced(g, 2, new SwapSettings()),
            _ => g,
        };
        CharacterSwapGraph.AssertNoDanglingLinks(g);

        var videos = g.Where(kv => Class(g, kv.Key) == "VHS_LoadVideo").Select(kv => kv.Key).ToHashSet();
        // ComfyUI 0.42 walks every linked output once a node finishes; a linked VHS audio output on a
        // soundless file throws before anything downstream runs.
        Assert.DoesNotContain(g, kv => kv.Value!["inputs"]!.AsObject()
            .Any(i => CharacterSwapGraph.IsLink(i.Value, out var src, out var slot) && videos.Contains(src) && slot == 2));
        foreach (var v in videos.Where(v => g.ContainsKey($"{v}_soundtrack")))
            Assert.Equal("EmptyAudio", Class(g, $"{v}_soundtrack"));
    }

    [Fact]
    public void Installed_names_win_over_upstream_names()
    {
        var installed = new[] { "h3-minimax/minimax_h3_ref2va_pruned_int8_convrot.safetensors", "qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors" };
        Assert.Equal(installed[0], CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultModel, installed));
        Assert.Equal(installed[1], CharacterSwapGraph.ResolveFile(CharacterSwapGraph.DefaultTextEncoder, installed, "qwen3vl_32b_minimax_h3"));
    }

    [Theory]
    [InlineData(6, 6, 1)]
    [InlineData(18, 6, 4)]
    [InlineData(0.1, 6, 0)]
    public void Clip_estimate_matches_upstream(double seconds, double clip, int expected) =>
        Assert.Equal(expected, CharacterSwapGraph.ClipCount(seconds, clip));

    private const string EmptyMask =
        "ComfyUI execution failed: node 68 (MiniMaxH3FantasticVideoEditLatent): The mask is empty on every frame, so nothing would change.";

    [Fact]
    public void Empty_mask_with_blank_roles_points_at_the_role_field()
    {
        var hint = CharacterSwapGraph.EmptyMaskHint(EmptyMask, advanced: true, new[] { "" }, "person", wholeVideo: true);
        Assert.NotNull(hint);
        Assert.Contains("Who do they replace?", hint);
        Assert.Contains("set the section", hint);
    }

    [Fact]
    public void Empty_mask_in_simple_mode_names_the_target()
    {
        var hint = CharacterSwapGraph.EmptyMaskHint(EmptyMask, advanced: false, new[] { "" }, "dancer", wholeVideo: false);
        Assert.Contains("\"dancer\"", hint);
    }

    [Fact]
    public void Describer_list_parses_into_role_and_look()
    {
        var people = CharacterSwapGraph.ParsePeople(
            "1. the dancer - woman in a black dress with a red sash\n2) the onlooker - man with a beard in a white kurta\nnoise\n3. the drummer");
        Assert.Equal(3, people.Count);
        Assert.Equal("the dancer", people[0].Role);
        Assert.Equal("woman in a black dress with a red sash", people[0].Look);
        Assert.Equal("the onlooker - man with a beard in a white kurta", people[1].Line);
        Assert.Equal("3. the drummer", people[2].Display);
    }

    [Theory]
    [InlineData("simple.api.json")]
    [InlineData("advanced.api.json")]
    public void Clip_preview_reads_the_trimmed_clip_and_saves_to_temp(string file)
    {
        var g = Load(file);
        CharacterSwapGraph.AddClipPreview(g);
        var preview = g[CharacterSwapGraph.ClipPreviewNode]!;
        Assert.Equal("VHS_VideoCombine", preview["class_type"]!.GetValue<string>());
        var inputs = preview["inputs"]!;
        Assert.True(CharacterSwapGraph.IsLink(inputs["images"], out var src, out var slot));
        Assert.Equal("MiniMaxH3LoopTrim", Class(g, src));
        Assert.Equal(0, slot);
        Assert.True(CharacterSwapGraph.IsLink(inputs["audio"], out var audioSrc, out var audioSlot));
        Assert.Equal((src, 1), (audioSrc, audioSlot));
        Assert.False(inputs["save_output"]!.GetValue<bool>());
        Assert.StartsWith(CharacterSwapGraph.ClipPreviewPrefix, inputs["filename_prefix"]!.GetValue<string>());
        CharacterSwapGraph.AssertNoDanglingLinks(g);
    }

    private static string Linked(JsonObject g, JsonNode? value)
    {
        Assert.True(CharacterSwapGraph.IsLink(value, out var id, out _));
        return id;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Keep_clothes_regenerates_only_the_chosen_persons_head(bool advanced)
    {
        var settings = new SwapSettings { Roles = new() { "the dancer" }, Outfit = SwapOutfit.KeepVideo, Margin = SwapMargin.Normal };
        var g = advanced
            ? CharacterSwapGraph.PruneAdvanced(Load(CharacterSwapGraph.AdvancedFile), 1, settings)
            : CharacterSwapGraph.PruneSimple(Load(CharacterSwapGraph.SimpleFile), settings);

        // What H3 regenerates: the grown head mask, heads multiplied by the person mask.
        var edit = g.First(kv => Class(g, kv.Key) == "MiniMaxH3FantasticVideoEditLatent").Key;
        var grow = Linked(g, g[edit]!["inputs"]!["mask"]);
        Assert.Equal("head_only", Linked(g, g[grow]!["inputs"]!["mask"]));
        Assert.Equal(CharacterSwapGraph.MarginPixels(SwapMargin.Normal) / 2, g[grow]!["inputs"]!["expand"]!.GetValue<int>());
        Assert.Equal("head_masks", Linked(g, g["head_only"]!["inputs"]!["destination"]));
        Assert.Contains(Class(g, Linked(g, g["head_only"]!["inputs"]!["source"])), new[] { "H3PeopleMask", "H3RoleMask" });
        Assert.Equal(CharacterSwapGraph.HeadTrackText, g["head_text"]!["inputs"]!["text"]!.GetValue<string>());

        // The mannequin hides only that head, so the motion reference still shows the clothes.
        var mannequin = g.First(kv => Class(g, kv.Key) == "H3GreyMannequins").Key;
        Assert.Equal(grow, Linked(g, g[mannequin]!["inputs"]!["mask"]));

        // H3 reads the rewritten prompt; the pictures are described by face and hair only.
        var reference = g.First(kv => Class(g, kv.Key) == "MiniMaxH3ReferenceToVideo").Key;
        Assert.Equal($"keep_clothes_prompt_{CharacterSwapGraph.KeepClothesRewrites.Length}", Linked(g, g[reference]!["inputs"]!["prompt"]));
        var describe = g[CharacterSwapGraph.NodeByTitle(g, "Describe picture 1")]!["inputs"]!["prompt"]!.GetValue<string>();
        Assert.Contains("Do not mention clothing", describe);
        CharacterSwapGraph.AssertNoDanglingLinks(g);
    }

    [Fact]
    public void Keep_clothes_check_mask_shows_the_head_area()
    {
        var g = CharacterSwapGraph.PruneMask(Load(CharacterSwapGraph.MaskFile), false, SwapMargin.Normal, "woman", SwapOutfit.KeepVideo);
        Assert.Equal("head_only", Linked(g, g[CharacterSwapGraph.MaskNodes.GrowMask]!["inputs"]!["mask"]));
        Assert.Equal(CharacterSwapGraph.MaskNodes.PeopleMask, Linked(g, g["head_only"]!["inputs"]!["source"]));
    }

    [Fact]
    public void From_picture_leaves_the_graph_as_upstream_builds_it()
    {
        var g = CharacterSwapGraph.PruneSimple(Load(CharacterSwapGraph.SimpleFile), new SwapSettings { Roles = new() { "" } });
        Assert.Null(g["head_only"]);
        Assert.DoesNotContain(g, kv => Class(g, kv.Key) == "RegexReplace");
    }

    [Fact]
    public void Keep_clothes_rewrites_every_outfit_sentence_of_the_plan_prompt()
    {
        // Sentences as H3OneClickSwapPlan / H3AdvancedSwapPlan write them (from a run on the server).
        const string prompt =
            "They replace the person in <Video 1>, in every shot where that person appears. Their face, hair, body and clothing all come from <Picture 1>.\n" +
            "<Video 1> is the source footage being edited, a 5.875-second section at 24 fps, in which the people to replace are shown as smooth grey figures. " +
            "It supplies the exact camera framing. The grey figures supply movement only; faces, hair, skin and clothing come from the pictures.\n" +
            "<Subject 1> takes the place of the person. Identities and complete outfits come from the pictures.\n" +
            "<Subject 1> (all shots): fully_preserved - the referenced facial identity, hair, skin tone and complete outfit remain consistent while poses follow.\n" +
            "<Video 1> (edited source): partially_preserved - the replaced people's identities and outfits change; camera movement is retained.\n" +
            "Their outfits replace the original clothing completely; no face, hair, skin, costume, fabric pattern or accessory of the wrestler remains.";

        var text = prompt;
        foreach (var (pattern, replace) in CharacterSwapGraph.KeepClothesRewrites)
        {
            Assert.Single(Regex.Matches(text, pattern));
            text = Regex.Replace(text, pattern, Regex.Replace(replace, @"\\(\d)", "$$$1"));  // Python \1 → .NET $1
        }
        Assert.DoesNotContain("outfit", text.Replace("unchanged original clothing", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Only their face and hair come from <Picture 1>", text);
    }

    [Theory]
    [InlineData("middle-aged woman with dark skin, short dark hair, wearing a dark patterned sari", "woman")]
    [InlineData("muscular man with a beard in a dirty tank top", "man")]
    [InlineData("tall figure in a red cloak", "tall figure")]
    public void Simple_mode_target_is_a_short_noun(string look, string expected) =>
        Assert.Equal(expected, CharacterSwapGraph.SamTarget(look));

    [Fact]
    public void Current_clip_output_gives_clip_number_and_count()
    {
        Assert.Equal((3, 47), CharacterSwapGraph.ActiveClip(
            "{\"h3_chain_active_scene\": [{\"run_name\": \"swap_x\", \"clip_index\": 3, \"clip_count\": 47}]}"));
        Assert.Null(CharacterSwapGraph.ActiveClip("{\"text\": [\"saved\"]}"));
        Assert.Null(CharacterSwapGraph.ActiveClip(null));
    }

    [Fact]
    public void Current_clip_output_gives_the_run_folder() =>
        Assert.Equal("swap_x", CharacterSwapGraph.ActiveRunName(
            "{\"h3_chain_active_scene\": [{\"run_name\": \"swap_x\", \"clip_index\": 3, \"clip_count\": 47}]}"));

    // Shapes as the Context Loop pack's /runs and /checkpoints routes return them (trimmed).
    private const string Runs = """
        {"runs": [
          {"run_name": "swap_old", "modified_at": "2026-11-07T05:39:26Z", "scene_count": 47, "checkpoint_count": 2,
           "restorable": true, "sources": {"api_prompt": true, "workflow": true, "plan": true}},
          {"run_name": "swap_new", "modified_at": "2026-11-07T06:19:28Z", "scene_count": 47, "checkpoint_count": 3,
           "restorable": true, "sources": {"api_prompt": true, "workflow": true, "plan": true}},
          {"run_name": "swap_done", "modified_at": "2026-11-07T06:02:23Z", "scene_count": 2, "checkpoint_count": 2,
           "restorable": true, "sources": {"api_prompt": true, "workflow": false, "plan": true}},
          {"run_name": "swap_empty", "modified_at": "2026-11-07T05:09:14Z", "scene_count": null, "checkpoint_count": 0,
           "restorable": false, "sources": {"api_prompt": false, "workflow": false, "plan": false}},
          {"run_name": "story_x", "modified_at": "2026-11-07T07:00:00Z", "scene_count": 9, "checkpoint_count": 4,
           "restorable": true, "sources": {"api_prompt": true, "workflow": true, "plan": true}}
        ]}
        """;

    [Fact]
    public void Unfinished_runs_are_swaps_part_way_through_newest_first()
    {
        var runs = CharacterSwapGraph.UnfinishedRuns(Runs);
        Assert.Equal(new[] { "swap_new", "swap_old" }, runs.Select(r => r.RunName));
        Assert.Equal((3, 47, 6), (runs[0].Done, runs[0].Total, runs[0].Percent));
        Assert.Equal(new DateTime(2026, 11, 7, 6, 19, 28, DateTimeKind.Utc), runs[0].ModifiedUtc);
        Assert.Empty(CharacterSwapGraph.UnfinishedRuns(null));
        Assert.Empty(CharacterSwapGraph.UnfinishedRuns("not json"));
    }

    private static string Checkpoint(int scene, bool ready = true) =>
        "{\"scene\": " + scene + ", \"ready\": " + (ready ? "true" : "false") + ", \"video\": {\"filename\": \"clip_" + scene.ToString("0000") +
        ".abc.mp4\", \"subfolder\": \"h3_chains/swap_new/generation/clips\", \"type\": \"output\"}}";

    [Fact]
    public void Resume_starts_after_the_unbroken_run_of_saved_clips()
    {
        var (next, clips) = CharacterSwapGraph.SavedClips(
            $$"""{"run_name": "swap_new", "checkpoints": [{{Checkpoint(1)}}, {{Checkpoint(2)}}, {{Checkpoint(3)}}]}""");
        Assert.Equal(4, next);
        Assert.Equal(new[] { 1, 2, 3 }, clips.Select(c => c.Number));
        Assert.Equal("clip_0002.abc.mp4", clips[1].Filename);
        Assert.Equal("h3_chains/swap_new/generation/clips", clips[1].Subfolder);

        // A clip that is not ready, or a gap, ends what can be continued from.
        Assert.Equal(2, CharacterSwapGraph.SavedClips(
            $$"""{"checkpoints": [{{Checkpoint(1)}}, {{Checkpoint(2, ready: false)}}, {{Checkpoint(3)}}]}""").Next);
        Assert.Equal(3, CharacterSwapGraph.SavedClips(
            $$"""{"checkpoints": [{{Checkpoint(2)}}, {{Checkpoint(1)}}, {{Checkpoint(4)}}]}""").Next);
        Assert.Equal(1, CharacterSwapGraph.SavedClips("""{"checkpoints": []}""").Next);
        Assert.Equal(1, CharacterSwapGraph.SavedClips(null).Next);
    }

    [Theory]
    [InlineData("akkas-small.mp4", @"C:\Videos\akkas-small.mp4", true)]
    [InlineData("akkas-small (1).mp4", @"C:\Videos\Akkas-Small.MP4", true)]
    [InlineData("akkas-small.mp4", "/home/me/akkas-small.mov", false)]
    [InlineData("akkas.mp4", @"C:\Videos\akkas-small.mp4", false)]
    [InlineData("", @"C:\Videos\akkas-small.mp4", false)]
    public void Saved_swap_is_matched_to_the_local_video_by_name(string server, string local, bool same) =>
        Assert.Equal(same, CharacterSwapGraph.SameVideo(server, local));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resume_continues_the_saved_prompt_in_its_own_run_folder(bool advanced)
    {
        var s = new SwapSettings { Roles = { "the woman" } };
        var g = advanced
            ? CharacterSwapGraph.PruneAdvanced(Load(CharacterSwapGraph.AdvancedFile), 1, s)
            : CharacterSwapGraph.PruneSimple(Load(CharacterSwapGraph.SimpleFile), s);
        var video = g.First(kv => Class(g, kv.Key) == "VHS_LoadVideo").Key;
        CharacterSwapGraph.Inputs(g, video)["video"] = "akkas-small.mp4";
        Assert.Equal("akkas-small.mp4", CharacterSwapGraph.ChainVideo(g));

        CharacterSwapGraph.PrepareResume(g, "swap_new", 4);

        var start = CharacterSwapGraph.Inputs(g, g.Single(kv => Class(g, kv.Key) == "MiniMaxH3ChainLoopStart").Key);
        Assert.Equal(4, start["start_clip"]!.GetValue<int>());
        Assert.Equal("", start["scene_range"]!.GetValue<string>());
        Assert.False(start["verify_resume_history"]!.GetValue<bool>());
        var plan = CharacterSwapGraph.Inputs(g, g.Single(kv => Class(g, kv.Key) == "MiniMaxH3ChainPlanModern").Key);
        Assert.Equal("swap_new", plan["run_name"]!.GetValue<string>());
        Assert.True(g.ContainsKey(CharacterSwapGraph.ClipPreviewNode));
        CharacterSwapGraph.AssertNoDanglingLinks(g);
    }

    [Fact]
    public void Saved_prompt_with_python_nan_cache_keys_parses()
    {
        var prompt = CharacterSwapGraph.ParseSavedPrompt("""
            {"14": {"class_type": "VHS_LoadVideo", "inputs": {"video": "akkas-small.mp4", "note": "keeps: NaN text"}, "is_changed": [NaN]},
             "36": {"class_type": "X", "inputs": {"a": -Infinity, "b": [1, NaN]}, "is_changed": [ NaN ]}}
            """);
        Assert.NotNull(prompt);
        Assert.False(prompt!["14"]!.AsObject().ContainsKey("is_changed"));
        Assert.Equal("akkas-small.mp4", CharacterSwapGraph.ChainVideo(prompt));
        Assert.Equal("keeps: NaN text", prompt["14"]!["inputs"]!["note"]!.GetValue<string>());
        Assert.Null(prompt["36"]!["inputs"]!["a"]);
        Assert.Null(CharacterSwapGraph.ParseSavedPrompt("<html>"));
    }

    [Fact]
    public void Only_a_chain_can_be_resumed() =>
        Assert.Throws<InvalidOperationException>(() =>
            CharacterSwapGraph.PrepareResume(CharacterSwapGraph.PruneMask(Load(CharacterSwapGraph.MaskFile), false, SwapMargin.Normal, "person"), "swap_x", 2));

    [Fact]
    public void Same_role_listed_twice_is_one_person()
    {
        var people = CharacterSwapGraph.ParsePeople(
            "1. the woman - woman in a dark sari\n2. the man - bearded man in a tank top\n3. The woman - woman in a sari with a green shawl\n4. the man - bearded man");
        Assert.Equal(new[] { "the woman", "the man" }, people.Select(p => p.Role));
        Assert.Equal("woman in a dark sari", people[0].Look);
        Assert.Equal(2, people[1].Number);
    }

    [Fact]
    public void Find_people_graph_loads_twenty_frames_across_the_part_and_previews_the_list()
    {
        var source = Load(CharacterSwapGraph.AdvancedFile);
        var g = CharacterSwapGraph.BuildFindPeople(source, "clip.mp4", "qwen3vl_8b_fp8_scaled.safetensors", 10, 0, 196.5);

        var loader = CharacterSwapGraph.Inputs(g, CharacterSwapGraph.NodeByTitle(g, CharacterSwapGraph.AdvancedVideoTitle));
        Assert.Equal("clip.mp4", loader["video"]!.GetValue<string>());
        Assert.Equal(240, loader["skip_first_frames"]!.GetValue<int>());
        Assert.Equal(CharacterSwapGraph.PeopleFrames, loader["frame_load_cap"]!.GetValue<int>());
        Assert.Equal((int)(186.5 * 24 / 20), loader["select_every_nth"]!.GetValue<int>());
        Assert.Equal("PreviewAny", g[CharacterSwapGraph.PeopleOutput]!["class_type"]!.GetValue<string>());
        Assert.Equal("qwen3vl_8b_fp8_scaled.safetensors",
            CharacterSwapGraph.Inputs(g, CharacterSwapGraph.NodeByTitle(g, "Describer: Qwen3-VL 8B"))["clip_name"]!.GetValue<string>());
        // Nothing reads the loader's audio, so a soundless source cannot fail here.
        Assert.Equal(5, g.Count);
    }

    [Fact]
    public void Other_failures_get_no_hint() =>
        Assert.Null(CharacterSwapGraph.EmptyMaskHint("node 14 (VHS_LoadVideo): VHS failed to extract audio", true, new[] { "" }, "person", true));
}
