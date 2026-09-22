using System.Reflection;
using System.Text.Json.Nodes;

namespace FlipPix.Mobile.Services;

/// <summary>The graphs shipped inside the app, read fresh on each use so edits never leak between runs.</summary>
public static class Workflows
{
    public static JsonObject Load(string name)
    {
        var asm = typeof(Workflows).Assembly;
        using var stream = asm.GetManifestResourceStream("wf/" + name)
            ?? throw new FileNotFoundException("Workflow not bundled: " + name);
        return JsonNode.Parse(stream)!.AsObject();
    }

    /// <summary>Sets one input on one node; a missing node is a drifted graph and fails loudly.</summary>
    public static void Set(JsonObject graph, string nodeId, string input, JsonNode? value)
    {
        if (graph[nodeId] is not JsonObject node || node["inputs"] is not JsonObject inputs)
            throw new InvalidOperationException($"The bundled workflow has no node {nodeId}; it has drifted.");
        inputs[input] = value;
    }

    public static long RandomSeed() => Random.Shared.NextInt64(1, 999_999_999_999_999);
}
