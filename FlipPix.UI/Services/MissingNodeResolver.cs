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
