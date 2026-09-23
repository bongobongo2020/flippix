using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.Tests;

/// <summary>
/// ⚡ H3 Express's LoRA stack, spliced over each of the four authored graphs.
///
/// <para>The tab used to splice exactly one <c>LoraLoaderModelOnly</c> onto node 21 — the rgthree Power Lora
/// Loader all four stacks ship empty. A chain is the same move made N times, and the way it breaks is the
/// way every graft on this tab breaks: a node whose own <c>model</c> input ends up pointed at itself, or a
/// reader left on node 21 so half the samplers run through the LoRAs and half do not. Neither throws at
/// submit — the render just comes out wrong, on a tab that runs folders of films unattended overnight.</para>
///
/// <para>So these assertions build the real chain over the real files and check the wire: 21 is read by the
/// first row and by nothing else, each row reads the one above it, everything that read 21 before now reads
/// the <b>last</b> row, and no link anywhere points at a node that is not there.</para>
/// </summary>
public sealed class H3ExpressLoraStackTests
{
    /// <summary>The four graphs a stack can be rendered on. All of them carry node 21.</summary>
    public static TheoryData<string> Graphs() => new()
    {
        "h3-eros.json", "h3-singularity.json", "h3-taomate.json", "h3-bunny.json",
    };

    private const string PowerLora = "21";

    /// <summary>Internal, so the same reflection the MiniMax I2V stack tests use reaches it.</summary>
    private static readonly MethodInfo Splice =
        typeof(H3ExpressViewModel).GetMethod("SpliceLoraStack",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("H3ExpressViewModel.SpliceLoraStack is gone or is no longer " +
                                               "static — these tests splice it over the authored graphs.");

    private static JsonObject Graph(string file)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                "workflow", "video", "h3-minimax", file);
        Assert.True(File.Exists(path), $"the graph is not in the output: {path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static void Apply(JsonObject root, params (string Name, double Strength)[] stack) =>
        Splice.Invoke(null, new object[] { root, stack.ToList() });

    /// <summary>Every link in the graph, as (node, input, target, slot).</summary>
    private static IEnumerable<(string Node, string Input, string Target)> Links(JsonObject root)
    {
        foreach (var (id, node) in root)
        {
            var inputs = node?["inputs"]?.AsObject();
            if (inputs == null) continue;
            foreach (var (name, value) in inputs)
            {
                if (value is not JsonArray a || a.Count != 2) continue;
                if (a[0] is not JsonValue v || v.TryGetValue<string>(out var target) == false) continue;
                yield return (id, name, target!);
            }
        }
    }

    private static string Target(JsonObject root, string id, string input) =>
        root[id]!["inputs"]![input]!.AsArray()[0]!.GetValue<string>();

    [Theory]
    [MemberData(nameof(Graphs))]
    public void EveryGraphShipsThePowerLoraSeatEmpty(string file)
    {
        var root = Graph(file);
        Assert.Equal("Power Lora Loader (rgthree)", root[PowerLora]!["class_type"]!.GetValue<string>());
    }

    /// <summary>The chain is built in list order, each row reading the one above, the first reading 21.</summary>
    [Theory]
    [MemberData(nameof(Graphs))]
    public void TheRowsChainInListOrder(string file)
    {
        var root = Graph(file);
        Apply(root,
              ("H3/first.safetensors", 1.00),
              ("H3/second.safetensors", 0.65),
              ("H3/third.safetensors", 0.30));

        Assert.Equal(PowerLora, Target(root, "h3express_lora_1", "model"));
        Assert.Equal("h3express_lora_1", Target(root, "h3express_lora_2", "model"));
        Assert.Equal("h3express_lora_2", Target(root, "h3express_lora_3", "model"));

        Assert.Equal("H3/second.safetensors",
                     root["h3express_lora_2"]!["inputs"]!["lora_name"]!.GetValue<string>());
        Assert.Equal(0.65, root["h3express_lora_2"]!["inputs"]!["strength_model"]!.GetValue<double>());
        foreach (var i in new[] { 1, 2, 3 })
            Assert.Equal("LoraLoaderModelOnly", root[$"h3express_lora_{i}"]!["class_type"]!.GetValue<string>());
    }

    /// <summary>
    /// The whole point of retargeting before the chain exists. Everything that read node 21 — the guiders and
    /// scheduler on Eros, the sigma shift on Singularity, both legs of the TaoMate relay, both stages of
    /// BUNNY's split — must come out reading the <b>end</b> of the chain, and node 21 must be read by the
    /// first row alone. A reader left behind samples the bare checkpoint while the rest do not.
    /// </summary>
    [Theory]
    [MemberData(nameof(Graphs))]
    public void EveryReaderOfTheSeatMovesToTheEndOfTheChain(string file)
    {
        var before = Graph(file);
        var readers = Links(before).Where(l => l.Target == PowerLora)
                                   .Select(l => (l.Node, l.Input)).ToList();
        Assert.NotEmpty(readers);

        var root = Graph(file);
        Apply(root, ("H3/a.safetensors", 1.00), ("H3/b.safetensors", 0.80));

        foreach (var (node, input) in readers)
            Assert.Equal("h3express_lora_2", Target(root, node, input));

        var onSeat = Links(root).Where(l => l.Target == PowerLora).Select(l => l.Node).ToList();
        Assert.Equal(new[] { "h3express_lora_1" }, onSeat);
    }

    /// <summary>A single row is the old behaviour exactly: one loader between the seat and its readers.</summary>
    [Theory]
    [MemberData(nameof(Graphs))]
    public void OneRowIsTheOldSingleSplice(string file)
    {
        var before = Graph(file);
        var readers = Links(before).Where(l => l.Target == PowerLora)
                                   .Select(l => (l.Node, l.Input)).ToList();

        var root = Graph(file);
        Apply(root, ("H3/only.safetensors", 1.25));

        Assert.Equal(PowerLora, Target(root, "h3express_lora_1", "model"));
        foreach (var (node, input) in readers)
            Assert.Equal("h3express_lora_1", Target(root, node, input));
        Assert.False(root.ContainsKey("h3express_lora_2"));
    }

    /// <summary>An empty stack leaves the graph byte-for-byte as authored — the no-LoRA render.</summary>
    [Theory]
    [MemberData(nameof(Graphs))]
    public void AnEmptyStackDoesNotTouchTheGraph(string file)
    {
        var root = Graph(file);
        var before = root.ToJsonString();

        Apply(root);

        Assert.Equal(before, root.ToJsonString());
    }

    /// <summary>No link points at a node that is not in the graph — the check a dangling graft fails.</summary>
    [Theory]
    [MemberData(nameof(Graphs))]
    public void TheSplicedGraphHasNoDanglingLinks(string file)
    {
        var root = Graph(file);
        Apply(root,
              ("H3/a.safetensors", 1.00),
              ("H3/b.safetensors", 0.90),
              ("H3/c.safetensors", 0.80),
              ("H3/d.safetensors", 0.70),
              ("H3/e.safetensors", 0.60));

        var dangling = Links(root).Where(l => !root.ContainsKey(l.Target))
                                  .Select(l => $"{l.Node}.{l.Input} → {l.Target}")
                                  .ToList();
        Assert.True(dangling.Count == 0, "links point at nodes that are not there:\n  " +
                                          string.Join("\n  ", dangling));
    }

    /// <summary>
    /// No row reads itself. This is the failure the "retarget first" comment in the splice is about: retarget
    /// rewrites every reader of node 21, so a row added to the graph before that call has its own model input
    /// rewritten to point at itself, and ComfyUI reports a cycle rather than a bad LoRA.
    /// </summary>
    [Theory]
    [MemberData(nameof(Graphs))]
    public void NoRowReadsItself(string file)
    {
        var root = Graph(file);
        Apply(root, ("H3/a.safetensors", 1.00), ("H3/b.safetensors", 0.80), ("H3/c.safetensors", 0.60));

        var loops = Links(root).Where(l => l.Node == l.Target).Select(l => l.Node).ToList();
        Assert.True(loops.Count == 0, "these nodes read themselves: " + string.Join(", ", loops));
    }
}
