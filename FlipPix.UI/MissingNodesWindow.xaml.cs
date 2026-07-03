using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI
{
    /// <summary>
    /// Lets the user resolve missing ComfyUI custom nodes: installs the providing pack(s) into the
    /// local ComfyUI's custom_nodes folder and restarts ComfyUI so they load. DialogResult is true
    /// when the caller should re-validate and retry the workflow.
    /// </summary>
    public partial class MissingNodesWindow : Window
    {
        private readonly NodeInstallerService _installer;
        private readonly IAppLogger _logger;
        private readonly ObservableCollection<Row> _rows = new();
        private bool _busy;

        public MissingNodesWindow(IReadOnlyList<MissingNodeInfo> missing,
            NodeInstallerService installer, IAppLogger logger)
        {
            InitializeComponent();
            _installer = installer;
            _logger = logger;

            foreach (var n in missing)
                _rows.Add(new Row(n)
                {
                    Status = string.IsNullOrEmpty(n.RepoUrl) ? "No known pack" : "Ready to install"
                });

            NodesList.ItemsSource = _rows;

            var installable = _installer.CanInstallLocally();
            if (!installable)
            {
                InstallButton.IsEnabled = false;
                RetryButton.IsEnabled = true; // let the user retry after installing manually
                IntroText.Text =
                    "This workflow uses the custom node(s) below, which the connected ComfyUI doesn't have. " +
                    "Automatic install works only for a local ComfyUI, so install the pack(s) listed below on " +
                    "the server (e.g. via ComfyUI-Manager → \"Install Missing Custom Nodes\"), restart ComfyUI, then click Retry.";
                foreach (var r in _rows)
                    r.Status = string.IsNullOrEmpty(r.Info.RepoUrl) ? "Search in ComfyUI-Manager" : r.Info.RepoUrl;
            }
            else if (!_installer.GitAvailable())
            {
                InstallButton.IsEnabled = false;
                RetryButton.IsEnabled = true;
                IntroText.Text =
                    "This workflow uses the custom node(s) below, which the connected ComfyUI doesn't have. " +
                    "Git isn't available to clone the packs automatically — install git (or use ComfyUI-Manager), " +
                    "install the pack(s) below, restart ComfyUI, then click Retry.";
                foreach (var r in _rows)
                    r.Status = string.IsNullOrEmpty(r.Info.RepoUrl) ? "Search in ComfyUI-Manager" : r.Info.RepoUrl;
            }
            else
            {
                InstallButton.IsEnabled = _rows.Any(r => !string.IsNullOrEmpty(r.Info.RepoUrl));
                if (!InstallButton.IsEnabled)
                    SetStatus("Couldn't identify the pack(s) for these nodes. Install them via ComfyUI-Manager, then click Retry.");
                RetryButton.IsEnabled = true;
            }
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            var targets = _rows.Where(r => !r.Installed && !string.IsNullOrEmpty(r.Info.RepoUrl)).ToList();
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
                    var result = await _installer.InstallAsync(row.Info, log, ct);
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
                    await Dispatcher.InvokeAsync(() => { DialogResult = true; Close(); });
                }
                else
                {
                    SetStatus("Nodes installed, but ComfyUI couldn't be restarted automatically. " +
                              "Restart ComfyUI, then click Retry.");
                }
            });
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            // Re-validate & retry the submission (the missing nodes may now be installed).
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // --- helpers ---

        private async Task RunBusyAsync(Func<CancellationToken, Task> work)
        {
            _busy = true;
            InstallButton.IsEnabled = RetryButton.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;
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
                Progress.Visibility = Visibility.Collapsed;
                InstallButton.IsEnabled = _rows.Any(r => !r.Installed && !string.IsNullOrEmpty(r.Info.RepoUrl))
                                          && _installer.CanInstallLocally() && _installer.GitAvailable();
                RetryButton.IsEnabled = true;
            }
        }

        private void SetStatus(string text) => Dispatcher.Invoke(() => StatusText.Text = text);

        /// <summary>Row bound to the ListView; raises change notifications for live status updates.</summary>
        private sealed class Row : INotifyPropertyChanged
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
