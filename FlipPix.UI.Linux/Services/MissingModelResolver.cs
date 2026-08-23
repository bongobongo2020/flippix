using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.UI.Linux.Windows;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>
    /// UI implementation of <see cref="IMissingModelResolver"/>. First tries to satisfy missing
    /// models silently from folders the user previously located, then shows
    /// <see cref="MissingModelsWindow"/> for anything still missing.
    ///
    /// <para>Ported from the WPF resolver; the dialog marshalling is Avalonia's — the HTTP client may
    /// call this from any thread, so the window is shown through <c>Dispatcher.UIThread</c> with an
    /// async <c>ShowDialog</c> instead of WPF's blocking modal.</para>
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

        private async Task<bool> ShowDialogAsync(IReadOnlyList<MissingModelInfo> missing, CancellationToken ct)
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
                _logger.LogError(ex, "Failed to show missing-models dialog");
                return false;
            }
        }

        private async Task<bool> ShowCoreAsync(IReadOnlyList<MissingModelInfo> missing)
        {
            var owner = ActiveWindow();
            if (owner == null)
            {
                _logger.LogWarning("No window available to own the missing-models dialog.");
                return false;
            }

            var window = new MissingModelsWindow(missing, _installer, _logger);
            var result = await window.ShowDialog<bool?>(owner);
            return result == true;
        }

        /// <summary>The window a dialog should sit on: the active one, else the main window.</summary>
        internal static Window? ActiveWindow() =>
            Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.Windows.FirstOrDefault(w => w.IsActive)
                  ?? desktop.MainWindow
                : null;
    }
}
