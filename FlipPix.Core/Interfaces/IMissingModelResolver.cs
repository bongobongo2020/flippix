using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Models;

namespace FlipPix.Core.Interfaces;

/// <summary>
/// Resolves model files a workflow needs but the connected ComfyUI is missing, by downloading
/// them or copying them in from a folder the user points at. Implemented in the UI layer (shows a
/// dialog); injected into the ComfyUI HTTP client so a missing model can be fixed mid-submit
/// instead of failing the workflow.
/// </summary>
public interface IMissingModelResolver
{
    /// <summary>
    /// Attempts to make every model in <paramref name="missing"/> available to ComfyUI. May prompt
    /// the user (download vs. locate folder) and persist a located folder so future misses resolve
    /// silently. Returns true if the caller should re-validate and retry the submission, false to
    /// surface the original missing-models error.
    /// </summary>
    Task<bool> TryResolveAsync(IReadOnlyList<MissingModelInfo> missing, CancellationToken cancellationToken = default);
}
