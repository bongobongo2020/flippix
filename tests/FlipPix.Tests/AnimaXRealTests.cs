using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlipPix.UI.ViewModels;

namespace FlipPix.Tests;

/// <summary>
/// "Anima X real": Anima draws the image, FIBO 2 edit converts it into a photo, MOSS Real (base)
/// polishes that. The graph must chain the three in that order, and the MOSS prompt must not ask for anime.
/// </summary>
public sealed class AnimaXRealTests
{
    private static JsonElement Graph() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "anima", "Anima-X-Real.json"))).RootElement;

    private static string Link(JsonElement graph, string node, string input) =>
        graph.GetProperty(node).GetProperty("inputs").GetProperty(input)[0].GetString()!;

    [Fact]
    public void Anima_ThenFibo2Edit_ThenMossPolish()
    {
        var g = Graph();

        Assert.Equal("60:8", Link(g, "24", "images.image_1")); // FIBO 2 edits the Anima render
        Assert.Equal("24", Link(g, "25", "latent_image"));      // FIBO 2 sampler ← its own encoder
        Assert.Equal("25", Link(g, "26", "samples"));           // FIBO 2 decode
        Assert.Equal("26", Link(g, "70", "image"));             // ImageScale ← FIBO 2 photo
        Assert.Equal("70", Link(g, "71", "pixels"));            // VAEEncode ← ImageScale
        Assert.Equal("3", Link(g, "71", "vae"));                // FLUX.2 VAE, which MOSS decodes with
        Assert.Equal("71", Link(g, "10", "latent_image"));      // MOSS SamplerCustom ← VAEEncode
        Assert.Equal("11", Link(g, "12", "images"));            // only the polished image is saved

        Assert.Contains("<image_1>", g.GetProperty("24").GetProperty("inputs").GetProperty("prompt").GetString());

        // FIBO 2 sets the layout; MOSS finishes the photo. At 0.85+ MOSS redraws the scene.
        var denoise = g.GetProperty("14").GetProperty("inputs").GetProperty("denoise").GetDouble();
        Assert.InRange(denoise, 0.75, 0.82);
    }

    [Fact]
    public void Graph_HasNoDanglingLinks_AndOneSave()
    {
        var g = Graph();
        var ids = g.EnumerateObject().Select(p => p.Name).ToHashSet();

        foreach (var node in g.EnumerateObject())
            foreach (var input in node.Value.GetProperty("inputs").EnumerateObject())
                if (input.Value.ValueKind == JsonValueKind.Array)
                    Assert.Contains(input.Value[0].GetString()!, ids);

        Assert.Single(g.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "SaveImage");
    }

    [Fact]
    public void RefinePrompt_DropsAnimaOnlyTags_AndKeepsTheScene()
    {
        var prompt = ImageAnalyzerViewModel.BuildAnimaXRealRefinePrompt(
            "masterpiece, best quality, score_7, safe, year 2025, @some artist, anime coloring, "
            + "1girl, solo, long brown hair, green sports bra, fighting stance, park");

        Assert.StartsWith("A high quality photorealistic photograph", prompt);
        Assert.Contains("a woman, long brown hair, green sports bra, fighting stance, park", prompt);
        foreach (var dropped in new[] { "masterpiece", "score_7", "safe,", "year 2025", "@some artist", "anime", "1girl", "solo" })
            Assert.DoesNotContain(dropped, prompt);
    }

    [Theory]
    [InlineData("masterpiece, 1girl, solo, red scarf", "a real woman")]
    [InlineData("score_7, 1boy, white shirt", "a real man")]
    [InlineData("1girl, 1boy, cafe", "a real woman and a real man")]
    [InlineData("scenery, no humans, forest", "real people")]
    public void FiboPrompt_NamesTheRealSubject_AndEditsTheImage(string animaPrompt, string subject)
    {
        var prompt = ImageAnalyzerViewModel.BuildAnimaXRealFiboPrompt(animaPrompt);

        Assert.StartsWith($"Recreate <image_1> as a real, unedited camera photograph of {subject}.", prompt);
        Assert.Contains("background included", prompt);
        Assert.Contains("Keep the exact same pose", prompt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Canvases_KeepTheAspect_InMultiplesOf16(int aspect)
    {
        var (aw, ah, rw, rh) = ImageAnalyzerViewModel.AnimaXRealResolution(aspect);

        Assert.Equal(aw * rh, ah * rw);
        Assert.True(rw > aw && rh > ah);
        Assert.Equal(0, rw % 16);
        Assert.Equal(0, rh % 16);
    }
}
