using System.Text.Json.Nodes;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// <see cref="CastPhotoWorkflows.BuildAsync"/> for the graphs ⚡ H3 Express can photograph its cast with. Each one must
/// carry the portrait prompt, the seed and the save prefix the photo is found by, and nothing the save does not read:
/// a preview or a second save left in the graph would run too, and Klein's refine branch fails without its input image.
/// </summary>
public sealed class CastPhotoWorkflowsTests
{
    private const string Prompt = "A full-length character portrait photograph of a tall man in a navy suit.";
    private const string Prefix = "h3_express/cast_1_20260912120000";
    private const long Seed = 123456789;

    private static async Task<(JsonObject Root, string SaveNode)> Build(string engine)
    {
        var (json, saveNode) = await CastPhotoWorkflows.BuildAsync(engine, Prefix, Seed, Prompt, _ => { });
        return (JsonNode.Parse(json)!.AsObject(), saveNode);
    }

    private static JsonObject Inputs(JsonObject root, string id) => root[id]!["inputs"]!.AsObject();

    [Theory]
    [InlineData("krea2spicy")]
    [InlineData("ideogram")]
    [InlineData("qwen")]
    [InlineData("klein")]
    public async Task Every_engine_saves_under_the_prefix_with_no_dangling_links(string engine)
    {
        var (root, saveNode) = await Build(engine);

        Assert.Equal("SaveImage", root[saveNode]!["class_type"]!.GetValue<string>());
        Assert.Equal(Prefix, Inputs(root, saveNode)["filename_prefix"]!.GetValue<string>());
        Assert.Contains(Prompt, root.ToJsonString().Replace("\\u0027", "'"));

        foreach (var (id, node) in root)
            foreach (var (name, value) in node!["inputs"]!.AsObject())
                if (value is JsonArray { Count: 2 } link && link[0] is JsonValue from && from.TryGetValue<string>(out var source))
                    Assert.True(root.ContainsKey(source), $"{engine}: node {id}.{name} reads missing node {source}");
    }

    [Fact]
    public async Task Ideogram_gets_a_literal_portrait_canvas_and_a_neutral_look()
    {
        var (root, _) = await Build("ideogram");

        // The resolution node, its readout, the sigma plot and the preview are all gone; the prompt readout feeds
        // the text encoder, so it stays.
        foreach (var id in new[] { "189", "191", "192", "202" })
            Assert.False(root.ContainsKey(id), $"node {id} should have been pruned");
        Assert.True(root.ContainsKey("199"));

        Assert.Equal(1088, Inputs(root, "160")["width"]!.GetValue<int>());
        Assert.Equal(1600, Inputs(root, "160")["height"]!.GetValue<int>());
        Assert.Equal(Seed, Inputs(root, "197")["seed"]!.GetValue<long>());

        var builder = Inputs(root, "185");
        Assert.Equal(1088, builder["width"]!.GetValue<int>());
        Assert.Equal(Prompt, builder["high_level_description"]!.GetValue<string>());
        Assert.Equal(string.Empty, builder["aesthetics"]!.GetValue<string>());
        Assert.Equal("compact", builder["output_format"]!.GetValue<string>());
        Assert.Equal("normalized", builder["coord_mode"]!.GetValue<string>());
        Assert.Equal("yx", builder["bbox_order"]!.GetValue<string>());

        var element = JsonNode.Parse(builder["elements_data"]!.GetValue<string>())!.AsArray().Single()!;
        Assert.Equal(Prompt, element["desc"]!.GetValue<string>());
    }

    [Fact]
    public async Task Klein_loses_the_refine_branch_that_needs_an_input_image()
    {
        var (root, _) = await Build("klein");

        foreach (var id in new[] { "222", "223", "224", "225", "226", "228", "238", "239", "261", "266", "278", "323" })
            Assert.False(root.ContainsKey(id), $"node {id} should have been pruned");

        Assert.Equal(Prompt, Inputs(root, "10")["text"]!.GetValue<string>());
        Assert.Equal(Seed, Inputs(root, "12")["seed"]!.GetValue<long>());
        Assert.Equal(1600, Inputs(root, "11")["height"]!.GetValue<int>());
        Assert.True(root.ContainsKey("212") && root.ContainsKey("264"), "the authored LoRAs stay");
    }
}
