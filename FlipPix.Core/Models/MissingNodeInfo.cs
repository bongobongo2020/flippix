namespace FlipPix.Core.Models;

/// <summary>
/// One custom-node type a workflow references that the connected ComfyUI doesn't have loaded
/// (ComfyUI rejects the prompt with a "missing_node_type" error). Carries enough context for the
/// resolver to offer an automatic install (the git repo of the pack that provides the node).
/// </summary>
public class MissingNodeInfo
{
    /// <summary>The ComfyUI node class, e.g. "FluxResolutionNode".</summary>
    public string ClassType { get; set; } = string.Empty;

    /// <summary>The node's display title in the workflow (for diagnostics), e.g. "Flux Resolution Calc".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The git URL of the custom-node pack that provides this node, once resolved (from the running
    /// ComfyUI-Manager's node map or the built-in <c>NodeCatalog</c>). Empty when unknown.
    /// </summary>
    public string RepoUrl { get; set; } = string.Empty;

    /// <summary>Human-readable pack name for display, e.g. "ControlAltAI-Nodes". Empty when unknown.</summary>
    public string PackName { get; set; } = string.Empty;

    /// <summary>
    /// True when the providing pack is already present in ComfyUI's custom_nodes but its node class
    /// still isn't available — i.e. the pack is installed but failing to import (usually a missing
    /// Python/SDK dependency, e.g. the RTX nodes needing nvvfx). Reinstalling won't help, so the
    /// resolver must not offer it as an auto-installable fix (that just loops).
    /// </summary>
    public bool AlreadyInstalled { get; set; }
}
