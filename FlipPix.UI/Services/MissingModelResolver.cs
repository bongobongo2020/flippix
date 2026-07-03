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
    /// UI implementation of <see cref="IMissingModelResolver"/>. First tries to satisfy missing
    /// models silently from folders the user previously located, then shows
    /// <see cref="MissingModelsWindow"/> for anything still missing.
    /// </summary>
    public class MissingModelResolver : IMissingModelResolver
    {
        private readonly ModelInstallerService _installer;
        private readonly IAppLogger _logger;

        public MissingModelResolver(ModelInstallerService installer, IAppLogger logger)
        {
            _installer = installer;
            _logger = logger;
        }

        public async Task<bool> TryResolveAsync(IReadOnlyList<MissingModelInfo> missing, CancellationToken cancellationToken = default)
        {
            if (missing == null || missing.Count == 0) return false;

            // 1) Silent pass: copy anything we can find in already-remembered source folders so the
            //    user is only ever prompted once for a given folder.
            var remaining = await TrySilentCopyAsync(missing.ToList(), cancellationToken);
            if (remaining.Count == 0)
            {
                _logger.LogInfo("All missing models resolved silently from remembered folders.");
                return true;
            }

            // 2) Anything left needs the user: show the dialog on the UI thread.
            return await ShowDialogAsync(remaining, cancellationToken);
        }

        private async Task<List<MissingModelInfo>> TrySilentCopyAsync(
            List<MissingModelInfo> missing, CancellationToken ct)
        {
            var folders = _installer.PersistedSourceFolders;
            var modelsRoot = _installer.ResolveModelsRoot();
            if (folders.Count == 0 || string.IsNullOrEmpty(modelsRoot)) return missing;

            var remaining = new List<MissingModelInfo>();
            foreach (var m in missing)
            {
                ct.ThrowIfCancellationRequested();
                bool done = false;
                foreach (var folder in folders)
                {
                    var source = _installer.FindInFolder(folder, m);
                    if (source == null) continue;
                    try
                    {
                        var result = await _installer.CopyAsync(modelsRoot!, m, source, null, ct);
                        if (result is InstallResult.Copied)
                        {
                            _logger.LogInfo($"Silently copied {m.FileName} from {folder}.");
                            done = true;
                            break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Silent copy of {m.FileName} failed: {ex.Message}");
                    }
                }
                if (!done) remaining.Add(m);
            }
            return remaining;
        }

        private Task<bool> ShowDialogAsync(IReadOnlyList<MissingModelInfo> missing, CancellationToken ct)
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null)
            {
                _logger.LogWarning("No WPF application available to show the missing-models dialog.");
                return Task.FromResult(false);
            }

            return app.Dispatcher.Invoke(() =>
            {
                try
                {
                    var window = new MissingModelsWindow(missing, _installer, _logger);
                    var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                                ?? app.MainWindow;
                    if (owner != null && owner != window && owner.IsLoaded)
                        window.Owner = owner;
                    var result = window.ShowDialog();
                    return Task.FromResult(result == true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to show missing-models dialog");
                    return Task.FromResult(false);
                }
            });
        }
    }
}
