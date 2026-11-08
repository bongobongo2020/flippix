using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// The 🎞 Remake tab fills Viggle Remake's API exports per request (<see cref="RemakeGraph"/>) and writes the
/// six-section prompt itself from Qwen3-VL notes. These check the graphs keep no link to a removed node, the
/// values land where the Remake nodes read them, and the prompt keeps upstream's format.
/// </summary>
public sealed class RemakeGraphTests
{
    private static JsonObject Load(string file)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "video", "viggle-remake", file);
        Assert.True(File.Exists(path), $"the Remake graph is not in the output: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static JsonObject LoadPaint()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "qwen-edit", RemakeGraph.PaintWorkflow);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static string Class(JsonObject g, string id) => g[id]!["class_type"]!.GetValue<string>();

    private static RemakeRender Request(RemakeMode mode, int pictures, bool pins, bool audio, string? lora = null) => new()
    {
        Mode = mode,
        Video = "clip.mp4",
        HasAudio = audio,
        StartSeconds = 2.5,
        Frames = 124,
        ShortEdge = 480,
        Pictures = Enumerable.Range(1, pictures).Select(i => $"paint{i}.png").ToList(),
        Pins = pins ? Enumerable.Range(0, pictures).Select(i => i * 40).ToList() : Array.Empty<int>(),
        Prompt = "subject_definitions:\n...",
        Seed = 42,
        Model = lora == null ? "h3-minimax/" + RemakeGraph.MergedSwapModel : "minimax_h3_fl2va_bf16.safetensors",
        Lora = lora,
        TextEncoder = "te.safetensors",
        VideoVae = "v.safetensors",
        AudioVae = "a.safetensors",
        OutputName = "my clip!",
    };

    [Theory]
    [InlineData(RemakeMode.Swap, 1, true, true)]
    [InlineData(RemakeMode.Swap, 4, false, false)]
    [InlineData(RemakeMode.Edit, 2, true, false)]
    [InlineData(RemakeMode.Edit, 3, false, true)]
    public void Render_fills_upstream_graph(RemakeMode mode, int pictures, bool pins, bool audio)
    {
        var file = mode == RemakeMode.Swap ? RemakeGraph.SwapFile : RemakeGraph.EditFile;
        var g = RemakeGraph.BuildRender(Load(file), Request(mode, pictures, pins, audio));

        CharacterSwapGraph.AssertNoDanglingLinks(g);
        Assert.False(g.ContainsKey(RemakeGraph.Nodes.Lora));
        Assert.False(g.ContainsKey(RemakeGraph.Nodes.Components));
        Assert.Equal("VHS_LoadVideo", Class(g, RemakeGraph.Nodes.Video));
        var video = g[RemakeGraph.Nodes.Video]!["inputs"]!;
        Assert.Equal(60, video["skip_first_frames"]!.GetValue<int>());
        Assert.Equal(124, video["frame_load_cap"]!.GetValue<int>());
        Assert.Equal(24, video["force_rate"]!.GetValue<int>());

        var cond = g[RemakeGraph.Nodes.Conditioning]!["inputs"]!.AsObject();
        Assert.Equal(audio, cond.ContainsKey("audio"));
        for (var i = 1; i <= 4; i++) Assert.Equal(i <= pictures, cond.ContainsKey($"ref_{i}"));
        Assert.Equal(pins ? string.Join(", ", Enumerable.Range(0, pictures).Select(i => i * 40)) : string.Empty,
                     cond["pins"]!.GetValue<string>());
        Assert.Equal(124, cond["frames"]!.GetValue<int>());
        Assert.Equal(mode == RemakeMode.Swap ? RemakeGraph.SwapSigmas : RemakeGraph.EditSigmas,
                     g[RemakeGraph.Nodes.Sigmas]!["inputs"]!["sigmas"]!.GetValue<string>());
        Assert.Equal("viggle_remake/my_clip", g[RemakeGraph.Nodes.Save]!["inputs"]!["filename_prefix"]!.GetValue<string>());
        Assert.Equal(42, g[RemakeGraph.Nodes.Noise]!["inputs"]!["seed"]!.GetValue<long>());
    }

    [Fact]
    public void Render_keeps_the_bypass_lora_for_a_base_model()
    {
        var g = RemakeGraph.BuildRender(Load(RemakeGraph.SwapFile), Request(RemakeMode.Swap, 1, true, true, RemakeGraph.SwapLora));
        Assert.Equal("LoraLoaderBypassModelOnly", Class(g, RemakeGraph.Nodes.Lora));
        Assert.Equal(RemakeGraph.SwapLora, g[RemakeGraph.Nodes.Lora]!["inputs"]!["lora_name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RemakeGraph.SwapLora)]
    public void Hd_adds_a_latent_x2_finish_with_keyframes_off(string? lora)
    {
        var r = Request(RemakeMode.Swap, 2, true, true, lora);
        var g = RemakeGraph.BuildRender(Load(RemakeGraph.SwapFile), new RemakeRender
        {
            Mode = r.Mode, Video = r.Video, HasAudio = r.HasAudio, StartSeconds = r.StartSeconds, Frames = r.Frames,
            ShortEdge = 480, Pictures = r.Pictures, Pins = r.Pins, Prompt = r.Prompt, Seed = r.Seed, Model = r.Model,
            Lora = r.Lora, TextEncoder = r.TextEncoder, VideoVae = r.VideoVae, AudioVae = r.AudioVae, LatentUpscale = true,
        });

        CharacterSwapGraph.AssertNoDanglingLinks(g);
        Assert.Equal("MinimaxH3LatentUpscaler3D", Class(g, RemakeGraph.Nodes.Upscaler));
        Assert.Equal(2.0, g[RemakeGraph.Nodes.Upscaler]!["inputs"]!["mode.scale"]!.GetValue<double>());
        // The finish samples the same transformer as the draft, with the painted keyframes always dropped.
        var up = g[RemakeGraph.Nodes.UpModel]!["inputs"]!;
        Assert.Equal(1.0, up["keyframe_until"]!.GetValue<double>());
        Assert.Equal(g[RemakeGraph.Nodes.KeyframeUntil]!["inputs"]!["model"]!.ToJsonString(), up["model"]!.ToJsonString());
        Assert.All(RemakeGraph.FinishSigmas.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)),
                   s => Assert.True(s < 1.0));
        // Picture from the finish, soundtrack from the draft.
        Assert.Equal(RemakeGraph.Nodes.UpSample, g[RemakeGraph.Nodes.DecodeVideo]!["inputs"]!["samples"]![0]!.GetValue<string>());
        Assert.Equal(RemakeGraph.Nodes.Sample, g["18"]!["inputs"]!["samples"]![0]!.GetValue<string>());

        Assert.Throws<ArgumentException>(() => RemakeGraph.BuildRender(Load(RemakeGraph.SwapFile), new RemakeRender
        {
            Pictures = r.Pictures, Frames = 124, Model = "m", ShortEdge = 736, LatentUpscale = true,
        }));
    }

    [Fact]
    public void Render_rejects_bad_lengths_and_pins()
    {
        var template = Load(RemakeGraph.SwapFile);
        var ok = Request(RemakeMode.Swap, 2, true, true);
        Assert.Throws<ArgumentException>(() => RemakeGraph.BuildRender(template, new RemakeRender
        {
            Pictures = ok.Pictures, Frames = 125, Model = "m",
        }));
        Assert.Throws<ArgumentException>(() => RemakeGraph.BuildRender(template, new RemakeRender
        {
            Pictures = ok.Pictures, Pins = new[] { 3 }, Frames = 124, Model = "m",
        }));
    }

    [Fact]
    public void Frame_lengths_are_17n_plus_5()
    {
        Assert.All(RemakeGraph.FrameOptions, f => Assert.Equal(0, (f - 5) % 17));
        Assert.Equal(90, RemakeGraph.FrameOptions[0]);
        Assert.Equal(345, RemakeGraph.FrameOptions[^1]);
        Assert.Equal(226, RemakeGraph.LongestFit(9.5));
        Assert.Equal(0, RemakeGraph.LongestFit(3.0));
        Assert.Equal(124, RemakeGraph.Snap(345, RemakeGraph.MaxFramesAt736));
        Assert.Equal(12, RemakeGraph.PinFor(0.5, 124));
        Assert.Equal(123, RemakeGraph.PinFor(99, 124));
        Assert.Equal("00:09.250", RemakeGraph.Stamp(9.25));
    }

    [Fact]
    public void Paint_wires_one_image_per_photo()
    {
        var g = RemakeGraph.BuildPaint(LoadPaint(), "frame.png", new[] { "a.png", "b.png" }, "do it", 7);
        CharacterSwapGraph.AssertNoDanglingLinks(g);
        var enc = g[RemakeGraph.PaintNodes.Encode]!["inputs"]!.AsObject();
        Assert.Equal(new[] { "images.image_1", "images.image_2", "images.image_3" },
                     enc.Select(kv => kv.Key).Where(k => k.StartsWith("images.")).OrderBy(k => k).ToArray());
        Assert.False(g.ContainsKey(RemakeGraph.PaintNodes.Character));

        var alone = RemakeGraph.BuildPaint(LoadPaint(), "frame.png", Array.Empty<string>(), "do it", 7);
        CharacterSwapGraph.AssertNoDanglingLinks(alone);
        Assert.Single(alone[RemakeGraph.PaintNodes.Encode]!["inputs"]!.AsObject().Where(kv => kv.Key.StartsWith("images.")));
    }

    [Fact]
    public void Paint_instruction_names_each_photo()
    {
        var text = RemakeGraph.PaintInstruction(RemakeMode.Swap, new[]
        {
            (new RemakeCast("the pizza maker", "Pete", ""), true),
            (new RemakeCast("the woman on the left", "", "a robot in chrome armour"), false),
        }, "");
        Assert.Contains("the pizza maker in <image1> with the character from <image2>", text);
        Assert.Contains("the woman on the left in <image1> with a robot in chrome armour", text);
        Assert.DoesNotContain("<image3>", text);
    }

    [Fact]
    public void Notes_graph_has_no_dangling_links()
    {
        var g = RemakeGraph.BuildNotes("clip.mp4", 1, 124, "qwen.safetensors", RemakeMode.Swap,
            new[] { new RemakeCast("the dancer", "", "") }, new string?[] { "photo.png" });
        CharacterSwapGraph.AssertNoDanglingLinks(g);
        Assert.True(g.ContainsKey(RemakeGraph.SceneOutput));
        Assert.True(g.ContainsKey(RemakeGraph.TimelineOutput));
        Assert.True(g.ContainsKey(RemakeGraph.LookOutput(0)));
        Assert.Contains("<Subject 1>", g["ask_timeline"]!["inputs"]!["prompt"]!.GetValue<string>());
        Assert.Contains("picture 8 at 00:04.667", g["ask_timeline"]!["inputs"]!["prompt"]!.GetValue<string>());
        Assert.Equal(16, g["video"]!["inputs"]!["select_every_nth"]!.GetValue<int>());
        Assert.Equal(8, g["video"]!["inputs"]!["frame_load_cap"]!.GetValue<int>());
    }

    [Fact]
    public void Timeline_parses_times_and_cuts()
    {
        var beats = RemakeGraph.ParseTimeline(
            "At 00:00.000 <Subject 1> waves.\n2. At 00:03, the camera pans right.\nCUT At 00:06.500 a close-up of <Subject 1>.\nnoise\nAt 01:30 too late", 10);
        Assert.Equal(3, beats.Count);
        Assert.Equal(3, beats[1].Seconds, 3);
        Assert.True(beats[2].Cut);
    }

    [Theory]
    [InlineData(RemakeMode.Swap)]
    [InlineData(RemakeMode.Edit)]
    public void Prompt_keeps_the_six_sections(RemakeMode mode)
    {
        var notes = new RemakeNotes("A man tosses pizza dough in a park.", "realistic, sunny daylight, handheld",
            "At 00:00.000 <Subject 1> holds the dough.\nAt 00:02.000 <Subject 1> tosses it high.\nCUT At 00:04.000 a close-up of the dough.",
            new[] { "a plush pineapple mascot with green gloves" });
        var prompt = RemakeGraph.ComposePrompt(mode, 124,
            new[] { new RemakeFrame(0.5, true), new RemakeFrame(4.5, true) },
            new[] { new RemakeCast("the pizza maker", "Pete", "") },
            mode == RemakeMode.Edit ? "the park covered in deep snow" : "", notes);

        Assert.Empty(RemakeGraph.CheckPrompt(prompt, 2));
        Assert.Contains("matches <Picture 2>", prompt);
        Assert.DoesNotContain("dim", prompt);
        Assert.All(prompt, ch => Assert.True(ch < 128, $"non-ASCII {ch}"));
        if (mode == RemakeMode.Swap)
        {
            Assert.Contains("<Subject 1> is Pete, a plush pineapple mascot", prompt);
            Assert.Contains("[video editing + character replacement]", prompt);
            Assert.Contains("[Shot 2] At 00:04.000, the shot cuts to", prompt);
        }
        else
        {
            Assert.Contains("now the park covered in deep snow", prompt);
            Assert.DoesNotContain("holds the dough", prompt); // the describer's beats stay out of an edit
            Assert.DoesNotContain("[Shot 2]", prompt);
        }
    }

    [Fact]
    public void Check_prompt_flags_missing_pictures()
    {
        var problems = RemakeGraph.CheckPrompt("summary:\n<Picture 3>", 2);
        Assert.Contains(problems, p => p.Contains("subject_definitions"));
        Assert.Contains(problems, p => p.Contains("<Picture 1>"));
        Assert.Contains(problems, p => p.Contains("only 2"));
    }
}
