using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Windows;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>
    /// UI implementation of <see cref="IMissingNodeResolver"/>. Resolves the providing pack for each
    /// missing node (offline catalog + running ComfyUI-Manager map), then shows
    /// <see cref="MissingNodesWindow"/> so the user can install them and restart ComfyUI.
    ///
    /// <para>Ported from the WPF resolver; the dialog marshalling is Avalonia's — the HTTP client may
    /// call this from any thread, so the window is shown through <c>Dispatcher.UIThread</c> with an
    /// async <c>ShowDialog</c> instead of WPF's blocking modal.</para>
    /// </summary>
    public class MissingNodeResolver : IMissingNodeResolver
    {
        private readonly NodeInstallerService _installer;
        private readonly IAppLogger _logger;

        // Node classes we've already driven through an install+restart this session that were still
        // missing afterwards. Re-offering them just loops (the pack installs but its class never
        // appears — e.g. an import failure from a missing dependency), so we suppress the dialog for
        // them and let the submission surface the clear error instead.
        private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);

        public MissingNodeResolver(NodeInstallerService installer, IAppLogger logger)
        {
            _installer = installer;
            _logger = logger;
        }

        public async Task<bool> TryResolveAsync(IReadOnlyList<MissingNodeInfo> missing, CancellationToken cancellationToken = default)
        {
            if (missing == null || missing.Count == 0) return false;

            // Enrich anything the offline catalog didn't know from the running ComfyUI-Manager.
            try { await _installer.ResolveReposAsync(missing, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning($"Node repo resolution failed: {ex.Message}"); }

            // Flag packs that are already present in custom_nodes: their class is still missing, so
            // the pack is installed but failing to import. Also look up a known, specific pip
            // dependency (e.g. nvidia-vfx for the RTX nodes) that would fix such an import failure.
            foreach (var n in missing)
            {
                n.AlreadyInstalled = _installer.IsPackPresent(n);
                var dep = NodeCatalog.GetPipDependency(n.ClassType);
                if (dep != null)
                {
                    n.PipPackage = dep.Value.package;
                    n.PipIndexUrl = dep.Value.indexUrl;
                }
                if (n.AlreadyInstalled && string.IsNullOrEmpty(n.PipPackage))
                    _logger.LogWarning(
                        $"Node '{n.ClassType}' pack '{n.PackName}' is installed but not loaded (import failed). " +
                        "Reinstalling won't help — check the ComfyUI console for its missing dependency.");
            }

            // A node is worth offering if we haven't tried it this session AND either: we know a
            // specific pip dependency that fixes it, or we can install its pack (known repo, not
            // already present-but-broken — reinstalling a broken pack just loops).
            var installable = missing.Where(n =>
                !_attempted.Contains(n.ClassType)
                && (!string.IsNullOrEmpty(n.PipPackage)
                    || (!string.IsNullOrEmpty(n.RepoUrl) && !n.AlreadyInstalled))).ToList();

            if (installable.Count == 0)
            {
                // Nothing we can usefully auto-install. Don't re-show the dialog (that's the loop);
                // return false so the caller reports the missing node(s) with actionable text.
                _logger.LogInfo("No auto-installable missing nodes; surfacing the error instead of re-prompting.");
                return false;
            }

            foreach (var n in installable) _attempted.Add(n.ClassType);
            return await ShowDialogAsync(missing);
        }

        private async Task<bool> ShowDialogAsync(IReadOnlyList<MissingNodeInfo> missing)
        {
            try
            {
                // Avalonia's InvokeAsync unwraps a Task-returning func, so the UI-thread result
                // arrives here directly whether or not we were already on the UI thread.
                return Dispatcher.UIThread.CheckAccess()
                    ? await ShowCoreAsync(missing)
                    : await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(missing));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to show missing-nodes dialog");
                return false;
            }
        }

        private async Task<bool> ShowCoreAsync(IReadOnlyList<MissingNodeInfo> missing)
        {
            var owner = MissingModelResolver.ActiveWindow();
            if (owner == null)
            {
                _logger.LogWarning("No window available to own the missing-nodes dialog.");
                return false;
            }

            var window = new MissingNodesWindow(missing, _installer, _logger);
            var result = await window.ShowDialog<bool?>(owner);
            return result == true;
        }
    }
}
