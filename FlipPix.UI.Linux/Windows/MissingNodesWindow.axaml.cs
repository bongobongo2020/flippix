using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.Windows
{
    /// <summary>
    /// Lets the user resolve missing ComfyUI custom nodes: installs the providing pack(s) into the
    /// local ComfyUI's custom_nodes folder and restarts ComfyUI so they load. Closes with
    /// <c>true</c> when the caller should re-validate and retry the workflow.
    ///
    /// <para>Ported from the WPF window: the dialog result is Avalonia's <c>Close(bool)</c> over
    /// <c>ShowDialog&lt;bool?&gt;</c>.</para>
    /// </summary>
    public partial class MissingNodesWindow : Window
    {
        private readonly NodeInstallerService _installer;
        private readonly IAppLogger _logger;
        private readonly ObservableCollection<Row> _rows = new();
        private bool _busy;

        public MissingNodesWindow()
        {
            // Binds to the generated InitializeComponent(bool loadXaml = true, ...), which loads
            // the XAML and assigns the x:Name fields declared in the .axaml.
            InitializeComponent();
        }

        public MissingNodesWindow(IReadOnlyList<MissingNodeInfo> missing,
            NodeInstallerService installer, IAppLogger logger) : this()
        {
            _installer = installer;
            _logger = logger;

            foreach (var n in missing)
                _rows.Add(new Row(n)
                {
                    Status = !string.IsNullOrEmpty(n.PipPackage)
                        ? $"Needs {n.PipPackage} — will install"
                        : (n.AlreadyInstalled
                            ? "Installed but failed to load — see ComfyUI console"
                            : (string.IsNullOrEmpty(n.RepoUrl) ? "No known pack" : "Ready to install"))
                });

            NodesList.ItemsSource = _rows;

            var canLocal = _installer.CanInstallLocally();
            var hasPipFix = _rows.Any(r => !string.IsNullOrEmpty(r.Info.PipPackage));
            // A present-but-broken pack we DON'T know how to fix (no pip dependency) can't be repaired
            // by reinstalling — surface that; but if we have a pip fix for it, we can install it.
            var hasUnfixableBroken = _rows.Any(r => r.Info.AlreadyInstalled && string.IsNullOrEmpty(r.Info.PipPackage));

            if (hasPipFix)
                IntroText.Text =
                    "A node below is installed but its Python dependency is missing (e.g. the NVIDIA RTX node needs " +
                    "the nvidia-vfx / nvvfx package). FlipPix can install that dependency and restart ComfyUI. " +
                    "The download can be large.";
            else if (hasUnfixableBroken)
                IntroText.Text =
                    "One or more of these packs is already installed but failed to load in ComfyUI — usually a " +
                    "missing Python/SDK dependency. Reinstalling won't fix that: check the ComfyUI console for the " +
                    "import error, install the missing dependency (or remove that node from the workflow), then click Retry.";

            if (!canLocal)
            {
                // Remote server: we can't clone into custom_nodes or install into its Python from here.
                InstallButton.IsEnabled = false;
                RetryButton.IsEnabled = true;
                if (!hasUnfixableBroken && !hasPipFix)
                    IntroText.Text =
                        "This workflow uses the custom node(s) below, which the connected ComfyUI doesn't have. " +
                        "Automatic install works only for a local ComfyUI, so install the pack(s) listed below on " +
                        "the server (e.g. via ComfyUI-Manager → \"Install Missing Custom Nodes\"), restart ComfyUI, then click Retry.";
                foreach (var r in _rows.Where(r => !r.Info.AlreadyInstalled && string.IsNullOrEmpty(r.Info.PipPackage)))
                    r.Status = string.IsNullOrEmpty(r.Info.RepoUrl) ? "Search in ComfyUI-Manager" : r.Info.RepoUrl;
            }
            else if (!_installer.GitAvailable() && !hasPipFix)
            {
                // Local but no git, and nothing is pip-fixable (git is only needed to clone packs).
                InstallButton.IsEnabled = false;
                RetryButton.IsEnabled = true;
                if (!hasUnfixableBroken)
                    IntroText.Text =
                        "This workflow uses the custom node(s) below, which the connected ComfyUI doesn't have. " +
                        "Git isn't available to clone the packs automatically — install git (or use ComfyUI-Manager), " +
                        "install the pack(s) below, restart ComfyUI, then click Retry.";
                foreach (var r in _rows.Where(r => !r.Info.AlreadyInstalled && string.IsNullOrEmpty(r.Info.PipPackage)))
                    r.Status = string.IsNullOrEmpty(r.Info.RepoUrl) ? "Search in ComfyUI-Manager" : r.Info.RepoUrl;
            }
            else
            {
                InstallButton.IsEnabled = _rows.Any(IsInstallable);
                if (!InstallButton.IsEnabled)
                    SetStatus(hasUnfixableBroken
                        ? "The pack(s) are installed but failing to load — fix the dependency in ComfyUI's console (see above), then click Retry."
                        : "Couldn't identify the pack(s) for these nodes. Install them via ComfyUI-Manager, then click Retry.");
                RetryButton.IsEnabled = true;
            }
        }


        // A row is auto-fixable if we know a specific pip dependency for it, or we can install its
        // pack (known repo and it isn't already present-but-broken — reinstalling a broken pack loops).
        private static bool IsInstallable(Row r) =>
            !r.Installed && (
                !string.IsNullOrEmpty(r.Info.PipPackage)
                || (!r.Info.AlreadyInstalled && !string.IsNullOrEmpty(r.Info.RepoUrl)));

        private async void InstallButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var targets = _rows.Where(IsInstallable).ToList();
            if (targets.Count == 0)
            {
                SetStatus("Nothing to install automatically — install the pack(s) via ComfyUI-Manager, then click Retry.");
                return;
            }

            await RunBusyAsync(async ct =>
            {
                var log = new Progress<string>(s => SetStatus(s));
                bool anyInstalled = false;

                foreach (var row in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    row.Status = "Installing...";
                    // A pack that's present but broken for a known pip dependency is fixed by
                    // installing that package (targeted, torch-safe); otherwise clone the pack.
                    var result = !string.IsNullOrEmpty(row.Info.PipPackage)
                        ? await _installer.InstallPipDependencyAsync(row.Info, log, ct)
                        : await _installer.InstallAsync(row.Info, log, ct);
                    switch (result)
                    {
                        case NodeInstallResult.Installed:
                            row.Status = "Installed"; row.Installed = true; anyInstalled = true; break;
                        case NodeInstallResult.AlreadyPresent:
                            row.Status = "Already installed"; row.Installed = true; anyInstalled = true; break;
                        case NodeInstallResult.NoRepo:
                            row.Status = "No known pack"; break;
                        default:
                            row.Status = "Failed"; break;
                    }
                }

                if (!anyInstalled)
                {
                    SetStatus("No packs were installed. Try ComfyUI-Manager, then click Retry.");
                    return;
                }

                // Restart ComfyUI so the freshly-cloned nodes load, then signal a retry.
                var restarted = await _installer.RestartComfyUIAsync(s => SetStatus(s), ct);
                if (restarted)
                {
                    SetStatus("ComfyUI restarted with the new nodes. Retrying the workflow...");
                    Close(true);
                }
                else
                {
                    SetStatus("Nodes installed, but ComfyUI couldn't be restarted automatically. " +
                              "Restart ComfyUI, then click Retry.");
                }
            });
        }

        private void RetryButton_Click(object? sender, RoutedEventArgs e)
        {
            // Re-validate & retry the submission (the missing nodes may now be installed).
            Close(true);
        }

        private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

        // --- helpers ---

        private async Task RunBusyAsync(Func<CancellationToken, Task> work)
        {
            _busy = true;
            InstallButton.IsEnabled = RetryButton.IsEnabled = false;
            Progress.IsVisible = true;
            using var cts = new CancellationTokenSource();
            try
            {
                await work(cts.Token);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Node install failed");
                SetStatus($"Error: {ex.Message}");
            }
            finally
            {
                _busy = false;
                Progress.IsVisible = false;
                // Re-enable if anything is still fixable: pip fixes need only a local Python;
                // pack clones also need git.
                var gitOk = _installer.CanInstallLocally() && _installer.GitAvailable();
                InstallButton.IsEnabled = _installer.CanInstallLocally()
                    && _rows.Any(r => IsInstallable(r) && (!string.IsNullOrEmpty(r.Info.PipPackage) || gitOk));
                RetryButton.IsEnabled = true;
            }
        }

        private void SetStatus(string text) => StatusText.Text = text;

        /// <summary>Row bound to the list; raises change notifications for live status updates.</summary>
        public sealed class Row : INotifyPropertyChanged
        {
            private string _status = "";
            public MissingNodeInfo Info { get; }
            public Row(MissingNodeInfo info) { Info = info; }

            public string ClassType => Info.ClassType;
            public string PackName => string.IsNullOrEmpty(Info.PackName) ? "(unknown)" : Info.PackName;

            public bool Installed { get; set; }

            public string Status
            {
                get => _status;
                set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
