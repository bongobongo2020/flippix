using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Models;

namespace FlipPix.Core.Interfaces;

/// <summary>
/// Resolves custom-node packs a workflow needs but the connected ComfyUI is missing, by installing
/// them (git clone + pip install into ComfyUI's custom_nodes) and restarting ComfyUI. Implemented in
/// the UI layer (shows a dialog); injected into the ComfyUI HTTP client so a missing node can be
/// fixed mid-submit instead of failing the workflow with a dead-end "missing_node_type" error.
/// </summary>
public interface IMissingNodeResolver
{
    /// <summary>
    /// Attempts to make every node in <paramref name="missing"/> available to ComfyUI. May prompt the
    /// user, install the providing packs, and restart ComfyUI. Returns true if the caller should
    /// re-validate and retry the submission, false to surface the original missing-nodes error.
    /// </summary>
    Task<bool> TryResolveAsync(IReadOnlyList<MissingNodeInfo> missing, CancellationToken cancellationToken = default);
}
