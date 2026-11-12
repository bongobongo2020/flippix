using System.Text.Json;
using System.Text.Json.Nodes;
using FlipPix.ComfyUI.Http;
using FlipPix.Core.Services;

namespace FlipPix.ComfyUI.Setup;

/// <summary>One workflow file FlipPix ships, parsed once.</summary>
public sealed class WorkflowSource
{
    public WorkflowSource(string name, JsonElement root)
    {
        Name = name;
        Root = root;
    }

    /// <summary>Path relative to the workflow folder, with forward slashes, e.g. "image/zimage/z4k.json".</summary>
    public string Name { get; }

    public JsonElement Root { get; }
}

/// <summary>A custom-node class that some workflows use and the server hasn't loaded.</summary>
public sealed class RequiredNode
{
    public string ClassType { get; init; } = "";
    public string Title { get; set; } = "";
    public string RepoUrl { get; set; } = "";
    public string PackName { get; set; } = "";
    public SortedSet<string> Workflows { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A model file that some workflows load and the server doesn't list where they expect it.</summary>
public sealed class RequiredModel
{
    /// <summary>The value as the workflow names it, with the host's path separator.</summary>
    public string Name { get; init; } = "";

    /// <summary>ComfyUI models sub-folder ("loras", "vae", ...), or "" when it can't be inferred.</summary>
    public string Category { get; set; } = "";

    public string ClassType { get; init; } = "";

    /// <summary>
    /// The same file under another path on the server (same filename, different subfolder or case).
    /// FlipPix switches the workflow to it when it submits, so nothing has to be downloaded.
    /// </summary>
    public string? ServerMatch { get; set; }

    public SortedSet<string> Workflows { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string FileName => Path.GetFileName(Name.Replace('\\', '/'));

    /// <summary>Path under the models root with forward slashes, e.g. "loras/klein/x.safetensors".</summary>
    public string RelativePath
    {
        get
        {
            var name = Name.Replace('\\', '/').TrimStart('/');
            return string.IsNullOrEmpty(Category) ? name : $"{Category}/{name}";
        }
    }
}

/// <summary>How ready one workflow is on the scanned server.</summary>
public sealed class WorkflowReadiness
{
    public string Name { get; init; } = "";
    public int MissingNodes { get; set; }
    public int MissingModels { get; set; }

    /// <summary>Models the server has under another path; they are switched over at submit.</summary>
    public int RemappedModels { get; set; }

    public bool Ready => MissingNodes == 0 && MissingModels == 0;
}

/// <summary>Everything the shipped workflows need that the scanned server doesn't have.</summary>
public sealed class RequirementsReport
{
    public List<RequiredNode> MissingNodes { get; } = new();
    public List<RequiredModel> MissingModels { get; } = new();
    public List<WorkflowReadiness> Workflows { get; } = new();

    /// <summary>
    /// True when some model inputs sit on nodes the server hasn't loaded, so they can only be checked
    /// once those node packs are installed and the server is scanned again.
    /// </summary>
    public bool HasUncheckedModels { get; set; }

    public int ReadyWorkflows => Workflows.Count(w => w.Ready);
}

/// <summary>
/// Works out which custom nodes and model files FlipPix's shipped workflows need from a ComfyUI server,
/// by reading every workflow (API or UI format) against the server's /object_info. Pure: no I/O apart
/// from <see cref="LoadWorkflows"/>, so it is tested without a server.
/// </summary>
public static class WorkflowRequirementScanner
{
    internal static readonly string[] ModelFileExtensions =
        { ".safetensors", ".ckpt", ".pt", ".pth", ".bin", ".gguf", ".sft", ".onnx" };

    // Nodes that only exist in the editor (notes, reroutes, primitives, rgthree / KJ helpers). They never
    // reach the server, so they are never "missing".
    private static readonly HashSet<string> FrontendOnlyNodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Note", "MarkdownNote", "Reroute", "PrimitiveNode", "SetNode", "GetNode",
        "Reroute (rgthree)", "Fast Groups Muter (rgthree)", "Fast Groups Bypasser (rgthree)",
        "Fast Muter (rgthree)", "Fast Bypasser (rgthree)", "Mute / Bypass Repeater (rgthree)",
        "Mute / Bypass Relay (rgthree)", "Node Collector (rgthree)", "Fast Actions Button (rgthree)",
        "Bookmark (rgthree)", "Label (rgthree)", "Comment (rgthree)", "Note Plus (mtb)",
    };

    internal static bool IsFrontendOnly(string classType) =>
        FrontendOnlyNodes.Contains(classType)
        || Guid.TryParse(classType, out _)                                   // subgraph instance
        || classType.StartsWith("workflow>", StringComparison.Ordinal)       // legacy group node
        || classType.StartsWith("workflow/", StringComparison.Ordinal);

    internal static bool HasModelExtension(string s)
    {
        foreach (var ext in ModelFileExtensions)
            if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Reads every *.json under <paramref name="workflowRoot"/>. Files that aren't JSON workflows are skipped.
    /// </summary>
    public static List<WorkflowSource> LoadWorkflows(string workflowRoot)
    {
        var list = new List<WorkflowSource>();
        if (!Directory.Exists(workflowRoot)) return list;
        foreach (var file in Directory.EnumerateFiles(workflowRoot, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var rel = Path.GetRelativePath(workflowRoot, file).Replace('\\', '/');
                list.Add(new WorkflowSource(rel, doc.RootElement.Clone()));
            }
            catch
            {
                // Not a workflow (or not valid JSON): nothing to check.
            }
        }
        return list;
    }

    /// <summary>
    /// Compares the workflows against the server's /object_info. <paramref name="hostSep"/> is the
    /// server's path separator, because FlipPix rewrites model paths to it before it submits.
    /// </summary>
    public static RequirementsReport Analyze(IEnumerable<WorkflowSource> workflows, JsonElement objectInfo, char hostSep)
    {
        var report = new RequirementsReport();
        var nodes = new Dictionary<string, RequiredNode>(StringComparer.Ordinal);
        var models = new Dictionary<string, RequiredModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var wf in workflows)
        {
            var readiness = new WorkflowReadiness { Name = wf.Name };
            var wfNodes = new HashSet<string>(StringComparer.Ordinal);
            var wfModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wfRemapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var use in EnumerateNodeUses(wf.Root, objectInfo))
            {
                if (IsFrontendOnly(use.ClassType)) continue;

                if (!objectInfo.TryGetProperty(use.ClassType, out var oiNode))
                {
                    if (!nodes.TryGetValue(use.ClassType, out var rn))
                    {
                        var repo = NodeCatalog.GetRepoUrl(use.ClassType);
                        rn = new RequiredNode
                        {
                            ClassType = use.ClassType,
                            Title = use.Title,
                            RepoUrl = repo ?? "",
                            PackName = repo != null ? NodeCatalog.PackNameFromRepo(repo) : "",
                        };
                        nodes[use.ClassType] = rn;
                    }
                    rn.Workflows.Add(wf.Name);
                    wfNodes.Add(use.ClassType);
                    if (use.StringValues.Any(v => HasModelExtension(v.value))) report.HasUncheckedModels = true;
                    continue;
                }

                if (!oiNode.TryGetProperty("input", out var oiInput)) continue;
                var modelCombos = ModelCombos(oiInput);
                if (modelCombos.Count == 0) continue;

                foreach (var (inputName, rawValue) in use.StringValues)
                {
                    if (!HasModelExtension(rawValue)) continue;
                    var value = NormalizeSeparators(rawValue, hostSep);

                    // API format names the input; UI format only gives the widget values in order, so
                    // check the value against every model list on that node.
                    List<(string name, List<string> options)> candidates = inputName != null
                        ? modelCombos.Where(c => c.name == inputName).ToList()
                        : modelCombos;
                    if (candidates.Count == 0) continue;
                    if (candidates.Any(c => c.options.Contains(value))) continue;

                    var allOptions = candidates.SelectMany(c => c.options).ToList();
                    var match = FindServerMatch(value, allOptions);
                    var resolvedInput = inputName ?? (candidates.Count == 1 ? candidates[0].name : "");

                    if (!models.TryGetValue(value, out var rm))
                    {
                        rm = new RequiredModel
                        {
                            Name = value,
                            ClassType = use.ClassType,
                            Category = InferCategory(use.ClassType, resolvedInput),
                            ServerMatch = match,
                        };
                        models[value] = rm;
                    }
                    rm.Workflows.Add(wf.Name);
                    if (match != null) wfRemapped.Add(value);
                    else wfModels.Add(value);
                }
            }

            readiness.MissingNodes = wfNodes.Count;
            readiness.MissingModels = wfModels.Count;
            readiness.RemappedModels = wfRemapped.Count;
            report.Workflows.Add(readiness);
        }

        report.MissingNodes.AddRange(nodes.Values
            .OrderByDescending(n => n.Workflows.Count).ThenBy(n => n.ClassType, StringComparer.OrdinalIgnoreCase));
        report.MissingModels.AddRange(models.Values
            .OrderBy(m => m.ServerMatch != null).ThenByDescending(m => m.Workflows.Count)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase));
        report.Workflows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return report;
    }

    /// <summary>
    /// The server's copy of <paramref name="wanted"/> when the server lists the same filename under another
    /// path (another subfolder, or different case): e.g. a workflow asking for "ltx2/x.safetensors" on a
    /// server that has "LTX-23/x.safetensors". Null when the filename isn't there, or <paramref name="wanted"/>
    /// is listed as-is. When several copies exist, the one sharing the longest path tail with the request wins.
    /// </summary>
    public static string? FindServerMatch(string wanted, IReadOnlyCollection<string> options)
    {
        if (string.IsNullOrEmpty(wanted) || options.Count == 0) return null;
        if (options.Contains(wanted)) return null;

        var leaf = Leaf(wanted);
        var exact = options.Where(o => Leaf(o) == leaf).ToList();
        var pool = exact.Count > 0
            ? exact
            : options.Where(o => string.Equals(Leaf(o), leaf, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pool.Count == 0) return null;
        if (pool.Count == 1) return pool[0];

        var w = Norm(wanted);
        return pool
            .OrderByDescending(o => CommonTailLength(Norm(o), w))
            .ThenBy(o => o.Length)
            .ThenBy(o => o, StringComparer.Ordinal)
            .First();

        static string Leaf(string s) => s.Substring(Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\')) + 1);
        static string Norm(string s) => s.Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// The models sub-folder for a node's model input: the submit-time rules first, then the input's own
    /// name for loaders those rules don't know ("text_encoder" on an LTX loader, "vae_name" ...).
    /// </summary>
    internal static string InferCategory(string classType, string inputName)
    {
        var category = ComfyUIHttpClient.GetModelCategory(classType, inputName);
        if (category.Length > 0) return category;
        var n = (inputName ?? "").ToLowerInvariant();
        if (n.Contains("text_encoder") || n.Contains("clip") || n.Contains("gemma") || n.Contains("t5")) return "text_encoders";
        if (n.Contains("vae")) return "vae";
        if (n.Contains("lora")) return "loras";
        if (n.Contains("upscale")) return "upscale_models";
        if (n.Contains("ckpt") || n.Contains("checkpoint")) return "checkpoints";
        if (n.Contains("unet") || n.Contains("diffusion") || n.Contains("transformer")) return "diffusion_models";
        return "";
    }

    private static int CommonTailLength(string a, string b)
    {
        int n = 0;
        while (n < a.Length && n < b.Length && a[a.Length - 1 - n] == b[b.Length - 1 - n]) n++;
        return n;
    }

    /// <summary>
    /// Switches every model input in an API-format prompt that the server doesn't list to the server's copy
    /// of the same file (see <see cref="FindServerMatch"/>). Returns one line per input it changed.
    /// </summary>
    public static List<string> RemapModelsToServerCopies(JsonObject prompt, JsonElement objectInfo)
    {
        var changes = new List<string>();
        foreach (var node in prompt)
        {
            if (node.Value is not JsonObject obj || obj["inputs"] is not JsonObject inputs) continue;
            if (obj["class_type"] is not JsonValue ctv || !ctv.TryGetValue<string>(out var classType)) continue;
            if (!objectInfo.TryGetProperty(classType, out var oiNode) || !oiNode.TryGetProperty("input", out var oiInput)) continue;
            var combos = ModelCombos(oiInput);
            if (combos.Count == 0) continue;

            var edits = new List<(string key, string value)>();
            foreach (var input in inputs)
            {
                if (input.Value is not JsonValue v || !v.TryGetValue<string>(out var s) || !HasModelExtension(s)) continue;
                var combo = combos.FirstOrDefault(c => c.name == input.Key);
                if (combo.options == null || combo.options.Contains(s)) continue;
                var match = FindServerMatch(s, combo.options);
                if (match != null) edits.Add((input.Key, match));
            }
            foreach (var (key, value) in edits)
            {
                changes.Add($"{classType}.{key}: '{inputs[key]}' -> '{value}' (same file on the server)");
                inputs[key] = value;
            }
        }
        return changes;
    }

    private static string NormalizeSeparators(string value, char hostSep)
    {
        var other = hostSep == '/' ? '\\' : '/';
        return value.IndexOf(other) >= 0 ? value.Replace(other, hostSep) : value;
    }

    /// <summary>Combo inputs on a node whose options look like model files.</summary>
    private static List<(string name, List<string> options)> ModelCombos(JsonElement oiInput)
    {
        var result = new List<(string, List<string>)>();
        foreach (var section in new[] { "required", "optional" })
        {
            if (!oiInput.TryGetProperty(section, out var sec) || sec.ValueKind != JsonValueKind.Object) continue;
            foreach (var spec in sec.EnumerateObject())
            {
                var options = ComboOptions(spec.Value);
                if (options == null) continue;
                // An empty model list still counts when the input name says it holds models.
                if (options.Any(HasModelExtension) || (options.Count == 0 && LooksLikeModelInput(spec.Name)))
                    result.Add((spec.Name, options));
            }
        }
        return result;
    }

    private static bool LooksLikeModelInput(string name) =>
        name.EndsWith("_name", StringComparison.OrdinalIgnoreCase)
        || name.Contains("model", StringComparison.OrdinalIgnoreCase)
        || name.Contains("lora", StringComparison.OrdinalIgnoreCase)
        || name.Contains("ckpt", StringComparison.OrdinalIgnoreCase);

    private static List<string>? ComboOptions(JsonElement spec)
    {
        if (spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() == 0) return null;
        var first = spec[0];
        JsonElement arr;
        if (first.ValueKind == JsonValueKind.Array) arr = first;
        else if (first.ValueKind == JsonValueKind.String
                 && string.Equals(first.GetString(), "COMBO", StringComparison.OrdinalIgnoreCase)
                 && spec.GetArrayLength() > 1 && spec[1].ValueKind == JsonValueKind.Object
                 && spec[1].TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            arr = opts;
        else return null;

        var list = new List<string>();
        foreach (var v in arr.EnumerateArray())
            if (v.ValueKind == JsonValueKind.String) list.Add(v.GetString()!);
        return list;
    }

    private readonly record struct NodeUse(string ClassType, string Title, List<(string? input, string value)> StringValues);

    /// <summary>The nodes a workflow would actually run, in either file format.</summary>
    private static IEnumerable<NodeUse> EnumerateNodeUses(JsonElement root, JsonElement objectInfo)
    {
        if (root.TryGetProperty("nodes", out var uiNodes) && uiNodes.ValueKind == JsonValueKind.Array)
            return UiNodeUses(root);
        return ApiNodeUses(root, objectInfo);
    }

    private static IEnumerable<NodeUse> ApiNodeUses(JsonElement prompt, JsonElement objectInfo)
    {
        var all = new Dictionary<string, JsonElement>();
        foreach (var p in prompt.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object
                && p.Value.TryGetProperty("class_type", out var ct) && ct.ValueKind == JsonValueKind.String)
                all[p.Name] = p.Value;
        if (all.Count == 0) yield break;

        // ComfyUI only runs what feeds an output node, so leftover loaders elsewhere in the file don't
        // count. A node class the server lacks can't say whether it is an output, so any such node
        // nothing else consumes is treated as one.
        var consumed = new HashSet<string>();
        foreach (var n in all.Values)
            foreach (var src in Links(n)) consumed.Add(src);

        var roots = new List<string>();
        foreach (var (id, n) in all)
        {
            var ct = n.GetProperty("class_type").GetString()!;
            if (objectInfo.TryGetProperty(ct, out var oi))
            {
                if (oi.TryGetProperty("output_node", out var on) && on.ValueKind == JsonValueKind.True) roots.Add(id);
            }
            else if (!consumed.Contains(id) && !IsFrontendOnly(ct)) roots.Add(id);
        }

        IEnumerable<string> ids = all.Keys;
        if (roots.Count > 0)
        {
            var reachable = new HashSet<string>();
            var stack = new Stack<string>(roots);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!reachable.Add(id) || !all.TryGetValue(id, out var n)) continue;
                foreach (var src in Links(n)) if (all.ContainsKey(src)) stack.Push(src);
            }
            ids = all.Keys.Where(reachable.Contains);
        }

        foreach (var id in ids)
        {
            var n = all[id];
            var title = n.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("title", out var t)
                        && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var values = new List<(string?, string)>();
            if (n.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object)
                foreach (var inp in inputs.EnumerateObject())
                    if (inp.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(inp.Value.GetString()))
                        values.Add((inp.Name, inp.Value.GetString()!));
            yield return new NodeUse(n.GetProperty("class_type").GetString()!, title, values);
        }
    }

    private static IEnumerable<string> Links(JsonElement node)
    {
        if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) yield break;
        foreach (var inp in inputs.EnumerateObject())
            if (inp.Value.ValueKind == JsonValueKind.Array && inp.Value.GetArrayLength() >= 1
                && inp.Value[0].ValueKind == JsonValueKind.String)
                yield return inp.Value[0].GetString()!;
    }

    private static IEnumerable<NodeUse> UiNodeUses(JsonElement root)
    {
        foreach (var n in UiNodes(root))
        {
            if (!n.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) continue;
            // mode 2 = muted, 4 = bypassed: the node never reaches the server.
            if (n.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.Number
                && (mode.GetInt32() == 2 || mode.GetInt32() == 4)) continue;

            var title = n.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var values = new List<(string?, string)>();
            if (n.TryGetProperty("widgets_values", out var wv))
            {
                if (wv.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in wv.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                            values.Add((null, v.GetString()!));
                }
                else if (wv.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in wv.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.Value.GetString()))
                            values.Add((p.Name, p.Value.GetString()!));
                }
            }
            yield return new NodeUse(typeEl.GetString()!, title, values);
        }
    }

    // Top-level nodes plus the nodes inside subgraph definitions (newer ComfyUI frontends).
    private static IEnumerable<JsonElement> UiNodes(JsonElement root)
    {
        if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            foreach (var n in nodes.EnumerateArray())
                if (n.ValueKind == JsonValueKind.Object) yield return n;

        if (root.TryGetProperty("definitions", out var defs) && defs.ValueKind == JsonValueKind.Object
            && defs.TryGetProperty("subgraphs", out var subs) && subs.ValueKind == JsonValueKind.Array)
            foreach (var sg in subs.EnumerateArray())
                if (sg.ValueKind == JsonValueKind.Object && sg.TryGetProperty("nodes", out var sn)
                    && sn.ValueKind == JsonValueKind.Array)
                    foreach (var n in sn.EnumerateArray())
                        if (n.ValueKind == JsonValueKind.Object) yield return n;
    }
}
