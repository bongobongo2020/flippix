using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// UI implementation of <see cref="IMissingNodeResolver"/>. Resolves the providing pack for each
    /// missing node (offline catalog + running ComfyUI-Manager map), then shows
    /// <see cref="MissingNodesWindow"/> so the user can install them and restart ComfyUI.
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
            // the pack is installed but failing to import — reinstalling can't fix that.
            foreach (var n in missing)
            {
                n.AlreadyInstalled = _installer.IsPackPresent(n);
                if (n.AlreadyInstalled)
                    _logger.LogWarning(
                        $"Node '{n.ClassType}' pack '{n.PackName}' is installed but not loaded (import failed). " +
                        "Reinstalling won't help — check the ComfyUI console for its missing dependency.");
            }

            // A node is only worth offering if we know its pack, it isn't already installed-but-broken,
            // and we haven't already tried it this session.
            var installable = missing.Where(n =>
                !string.IsNullOrEmpty(n.RepoUrl)
                && !n.AlreadyInstalled
                && !_attempted.Contains(n.ClassType)).ToList();

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

        private Task<bool> ShowDialogAsync(IReadOnlyList<MissingNodeInfo> missing)
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null)
            {
                _logger.LogWarning("No WPF application available to show the missing-nodes dialog.");
                return Task.FromResult(false);
            }

            return app.Dispatcher.Invoke(() =>
            {
                try
                {
                    var window = new MissingNodesWindow(missing, _installer, _logger);
                    var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                ?? app.MainWindow;
                    if (owner != null && owner != window && owner.IsLoaded)
                        window.Owner = owner;
                    var result = window.ShowDialog();
                    return Task.FromResult(result == true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to show missing-nodes dialog");
                    return Task.FromResult(false);
                }
            });
        }
    }
}
