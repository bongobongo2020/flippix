using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    /// Lets the user resolve missing ComfyUI models: download the catalog-known ones, or point at a
    /// folder that already has them (copied in, with extra_model_paths.yaml registration as a
    /// fallback). DialogResult is true when the caller should re-validate and retry the workflow.
    /// </summary>
    public partial class MissingModelsWindow : Window
    {
        private readonly ModelInstallerService _installer;
        private readonly IAppLogger _logger;
        private readonly ObservableCollection<Row> _rows = new();
        private bool _busy;

        public MissingModelsWindow(IReadOnlyList<MissingModelInfo> missing,
            ModelInstallerService installer, IAppLogger logger)
        {
            InitializeComponent();
            _installer = installer;
            _logger = logger;

            foreach (var m in missing)
                _rows.Add(new Row(m)
                {
                    Status = _installer.HasDownloadUrl(m) ? "Will download" : "Locate folder"
                });

            ModelsList.ItemsSource = _rows;
            DownloadButton.IsEnabled = _rows.Any(r => _installer.HasDownloadUrl(r.Info));
            UpdateDoneState();
        }

        private bool AllResolved => _rows.All(r => r.Installed);

        // Registered-but-not-copied rows need a ComfyUI restart before they count; only treat a
        // submission as immediately retryable when every row was downloaded/copied into place.
        private bool AllEffectiveNow => _rows.All(r => r.EffectiveNow);

        private void UpdateDoneState()
        {
            DoneButton.IsEnabled = !_busy && _rows.Any(r => r.Installed);
        }

        /// <summary>Ensures we have a writable models root, prompting for it on remote installs.</summary>
        private bool EnsureModelsRoot(out string modelsRoot)
        {
            modelsRoot = _installer.ResolveModelsRoot() ?? string.Empty;
            if (!string.IsNullOrEmpty(modelsRoot)) return true;

            var hint = _installer.IsRemoteServer()
                ? "Select the remote ComfyUI's \"models\" folder (a network path FlipPix can write to)."
                : "Select the ComfyUI \"models\" folder to install into.";
            System.Windows.MessageBox.Show(hint, "Choose models folder",
                MessageBoxButton.OK, MessageBoxImage.Information);

            var picked = BrowseFolder("Select the ComfyUI models folder");
            if (string.IsNullOrEmpty(picked)) return false;
            if (!_installer.TrySetModelsRoot(picked))
            {
                System.Windows.MessageBox.Show("That folder isn't accessible.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            modelsRoot = _installer.ResolveModelsRoot() ?? picked;
            return true;
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!EnsureModelsRoot(out var modelsRoot)) return;

            var targets = _rows.Where(r => !r.Installed && _installer.HasDownloadUrl(r.Info)).ToList();
            if (targets.Count == 0)
            {
                SetStatus("Nothing left to download — use \"Locate folder\" for the rest.");
                return;
            }

            await RunBusyAsync(async ct =>
            {
                Progress.Visibility = Visibility.Visible;
                foreach (var row in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    row.Status = "Downloading...";
                    SetStatus($"Downloading {row.FileName}...");
                    var progress = new Progress<(long done, long total)>(p => ReportBytes(row, "Downloading", p));
                    var result = await _installer.DownloadAsync(modelsRoot, row.Info, progress, ct);
                    ApplyResult(row, result);
                }
                Progress.Value = 0;
            });
        }

        private async void LocateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (!EnsureModelsRoot(out var modelsRoot)) return;

            var folder = BrowseFolder("Select a folder that contains the model file(s)");
            if (string.IsNullOrEmpty(folder)) return;

            await RunBusyAsync(async ct =>
            {
                Progress.Visibility = Visibility.Visible;
                _installer.RememberSourceFolder(folder);
                bool isModelsRoot = _installer.LooksLikeModelsRoot(folder);

                foreach (var row in _rows.Where(r => !r.Installed).ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    SetStatus($"Searching for {row.FileName}...");
                    row.Status = "Searching...";
                    var source = _installer.FindInFolder(folder, row.Info);
                    if (source != null)
                    {
                        row.Status = "Copying...";
                        var progress = new Progress<(long done, long total)>(p => ReportBytes(row, "Copying", p));
                        var result = await _installer.CopyAsync(modelsRoot, row.Info, source, progress, ct);
                        ApplyResult(row, result);
                    }
                    else if (isModelsRoot && _installer.RegisterExtraModelPath(folder))
                    {
                        // Fallback: the folder is a models root but our filename search missed it
                        // (odd layout). Register it so ComfyUI finds it after a restart.
                        row.Status = "Registered — restart ComfyUI";
                        row.Installed = true;
                        row.EffectiveNow = false;
                    }
                    else
                    {
                        row.Status = "Not found in folder";
                    }
                }
                Progress.Value = 0;
            });

            if (_rows.Any(r => r.Installed && !r.EffectiveNow))
            {
                SetStatus("Some models were registered with ComfyUI. Restart ComfyUI, then try again.");
            }
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            // True signals the caller to re-validate. If only registered (not copied) models remain,
            // re-validation will still report them until ComfyUI is restarted — that's expected.
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
            DownloadButton.IsEnabled = LocateButton.IsEnabled = DoneButton.IsEnabled = false;
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
                _logger.LogError(ex, "Model install failed");
                SetStatus($"Error: {ex.Message}");
            }
            finally
            {
                _busy = false;
                Progress.Visibility = Visibility.Collapsed;
                DownloadButton.IsEnabled = _rows.Any(r => !r.Installed && _installer.HasDownloadUrl(r.Info));
                LocateButton.IsEnabled = _rows.Any(r => !r.Installed);
                UpdateDoneState();
                if (AllResolved)
                    SetStatus(AllEffectiveNow
                        ? "All models installed. Click Done to continue."
                        : "Done — remember to restart ComfyUI for the registered models.");
            }
        }

        private void ApplyResult(Row row, InstallResult result)
        {
            switch (result)
            {
                case InstallResult.Downloaded:
                    row.Status = "Downloaded"; row.Installed = true; row.EffectiveNow = true; break;
                case InstallResult.Copied:
                    row.Status = "Copied"; row.Installed = true; row.EffectiveNow = true; break;
                case InstallResult.Registered:
                    row.Status = "Registered — restart ComfyUI"; row.Installed = true; row.EffectiveNow = false; break;
                case InstallResult.NotFound:
                    row.Status = "No download URL"; break;
                default:
                    row.Status = "Failed"; break;
            }
        }

        private void ReportBytes(Row row, string verb, (long done, long total) p)
        {
            Dispatcher.Invoke(() =>
            {
                if (p.total > 0)
                {
                    var pct = p.done * 100.0 / p.total;
                    Progress.Value = pct;
                    row.Status = $"{verb} {pct:0}% ({p.done / 1_048_576} / {p.total / 1_048_576} MB)";
                }
                else
                {
                    row.Status = $"{verb} {p.done / 1_048_576} MB";
                }
            });
        }

        private void SetStatus(string text) => Dispatcher.Invoke(() => StatusText.Text = text);

        private string? BrowseFolder(string description)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                ShowNewFolderButton = false
            };
            var last = _installer.PersistedSourceFolders.FirstOrDefault(Directory.Exists);
            if (last != null) dlg.SelectedPath = last;
            return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }

        /// <summary>Row bound to the ListView; raises change notifications for live status updates.</summary>
        private sealed class Row : INotifyPropertyChanged
        {
            private string _status = "";
            public MissingModelInfo Info { get; }
            public Row(MissingModelInfo info) { Info = info; }

            public string FileName => Info.FileName;
            public string Category => string.IsNullOrEmpty(Info.Category) ? "(unknown)" : Info.Category;

            public bool Installed { get; set; }
            public bool EffectiveNow { get; set; }

            public string Status
            {
                get => _status;
                set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
