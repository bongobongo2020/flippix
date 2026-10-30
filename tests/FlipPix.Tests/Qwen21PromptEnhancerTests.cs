using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// Qwen21PromptEnhancer keeps an empty rewrite away from the encoder: the image job always gets real
/// text through the switch's ORIGINAL route, whatever the rewriter returned.
/// </summary>
public sealed class Qwen21PromptEnhancerTests
{
    private const string UserPrompt = "a woman on a forest path";

    private static JsonElement Workflow(bool enhancer)
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "workflow", "image", "qwen", "qwen21-prompt-enhancer.json")))
            dir = Path.GetDirectoryName(dir) ?? throw new FileNotFoundException("workflow folder not found");

        var graph = JsonNode.Parse(File.ReadAllText(
            Path.Combine(dir, "workflow", "image", "qwen", "qwen21-prompt-enhancer.json")))!;
        graph["468"]!["inputs"]!["value"] = UserPrompt;
        graph["473"]!["inputs"]!["value"] = enhancer;
        return JsonSerializer.SerializeToElement(graph);
    }

    private static (string Prompt, bool Switch) ImageJob(JsonElement graph) =>
        (graph.GetProperty("468").GetProperty("inputs").GetProperty("value").GetString()!,
         graph.GetProperty("473").GetProperty("inputs").GetProperty("value").GetBoolean());

    private static Task<JsonElement> Resolve(JsonElement graph, Func<JsonElement, Task<string?>> run) =>
        Qwen21PromptEnhancer.ResolveAsync(graph, (g, _) => run(g), _ => { }, CancellationToken.None);

    [Fact]
    public async Task Rewrite_GoesThroughTheOriginalRoute()
    {
        var job = await Resolve(Workflow(true), _ => Task.FromResult<string?>("  a rewritten prompt\n"));

        Assert.Equal(("a rewritten prompt", false), ImageJob(job));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public async Task EmptyRewrite_FallsBackToTheOriginalPrompt(string? rewrite)
    {
        var job = await Resolve(Workflow(true), _ => Task.FromResult(rewrite));

        Assert.Equal((UserPrompt, false), ImageJob(job));
    }

    [Fact]
    public async Task FailedRewrite_FallsBackToTheOriginalPrompt()
    {
        var job = await Resolve(Workflow(true), _ => throw new InvalidOperationException("node threw"));

        Assert.Equal((UserPrompt, false), ImageJob(job));
    }

    [Fact]
    public async Task EnhancerOff_SubmitsNothingExtra()
    {
        var ran = false;
        var job = await Resolve(Workflow(false), _ =>
        {
            ran = true;
            return Task.FromResult<string?>("unused");
        });

        Assert.False(ran);
        Assert.Equal((UserPrompt, false), ImageJob(job));
    }

    [Fact]
    public async Task EnhancerJob_IsTheRewriterAlone()
    {
        JsonElement enhancerGraph = default;
        await Resolve(Workflow(true), g =>
        {
            enhancerGraph = g;
            return Task.FromResult<string?>("text");
        });

        Assert.Equal(new[] { "468", "469", "470", "471", "472", "475" },
            enhancerGraph.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("472", enhancerGraph.GetProperty("475").GetProperty("inputs").GetProperty("source")[0].GetString());
        Assert.Equal(UserPrompt, enhancerGraph.GetProperty("468").GetProperty("inputs").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowedByTheFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Qwen21PromptEnhancer.ResolveAsync(
            Workflow(true),
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<string?>("unused");
            },
            _ => { },
            cts.Token));
    }
}
