using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FlipPix.ComfyUI.Setup;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI
{
    /// <summary>
    /// Checks a ComfyUI server against every workflow FlipPix ships and gets it ready: installs missing
    /// node packs through ComfyUI-Manager, finds missing models already on the server (another folder),
    /// in a folder the user has, through Manager's model list, or downloads them onto the server's models
    /// share, and writes a script for whatever has to be done on the server itself. Opened after the
    /// server URL changes in Settings, and from Settings' "Check server setup" button.
    /// </summary>
    public partial class ServerSetupWindow : Window
    {
        private readonly SettingsService _settingsService;
        private readonly IAppLogger? _logger;
        private readonly string _baseUrl;
        private readonly ComfyServerSetupClient _client;
        private CancellationTokenSource? _cts;

        private ServerInfo? _info;
        private RequirementsReport? _report;
        private Dictionary<string, List<string>> _serverFolders = new();
        private Dictionary<string, FolderMapping> _folders = new();
        private readonly Dictionary<string, bool> _writable = new(StringComparer.OrdinalIgnoreCase);
        private List<ManagerPack> _packs = new();
        private List<ManagerModel> _managerModels = new();
        private Dictionary<string, List<FileInfo>> _localIndex = new();
        private readonly List<string> _extraShareHints = new();
        private readonly HashSet<string> _essentials;

        private readonly ObservableCollection<NodePackRow> _nodeRows = new();
        private readonly List<ModelRow> _allModelRows = new();
        private readonly ObservableCollection<ModelRow> _modelRows = new();

        public ServerSetupWindow(SettingsService settingsService, IAppLogger? logger, string baseUrl)
        {
            InitializeComponent();
            _settingsService = settingsService;
            _logger = logger;
            _baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
            _client = new ComfyServerSetupClient(_baseUrl);
            _essentials = LoadEssentials();

            NodesList.ItemsSource = _nodeRows;
            ModelsList.ItemsSource = _modelRows;
            ServerLine.Text = $"{_baseUrl} — connecting ...";

            Loaded += async (_, __) => await ScanAsync();
            Closed += (_, __) => { _cts?.Cancel(); _client.Dispose(); };
        }

        /// <summary>Opens the window for <paramref name="baseUrl"/>, owned by <paramref name="owner"/> when given.</summary>
        public static void Open(Window? owner, SettingsService settingsService, string baseUrl)
        {
            IAppLogger? logger = null;
            try
            {
                if (System.Windows.Application.Current is App app && app.Services != null)
                    logger = app.Services.GetService(typeof(IAppLogger)) as IAppLogger;
            }
            catch { /* logging is optional here */ }

            var window = new ServerSetupWindow(settingsService, logger, baseUrl);
            if (owner != null && owner.IsLoaded) window.Owner = owner;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }

        // ================================================================ scan

        private async Task ScanAsync()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetBusy(true, "Connecting to ComfyUI ...");
            Log($"Checking {_baseUrl}");

            try
            {
                _info = await _client.GetServerInfoAsync(ct);
                ServerLine.Text = DescribeServer(_info);
                Log(ServerLine.Text);

                SetStatus("Reading the server's nodes and models ...");
                var oiTask = _client.GetObjectInfoAsync(ct);
                var foldersTask = _client.GetFolderPathsAsync(ct);
                var mapTask = _client.GetNodeMappingsAsync(ct);
                var packsTask = _client.GetPackListAsync(ct);
                var mgrModelsTask = _client.GetManagerModelListAsync(ct);
                var workflowRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow");
                var workflowsTask = Task.Run(() => WorkflowRequirementScanner.LoadWorkflows(workflowRoot), ct);

                using var oi = await oiTask;
                var workflows = await workflowsTask;
                SetStatus($"Checking {workflows.Count} workflows against the server ...");
                var sep = _info.PathSeparator;
                _report = await Task.Run(() => WorkflowRequirementScanner.Analyze(workflows, oi.RootElement, sep), ct);

                _serverFolders = await foldersTask;
                var map = await mapTask;
                _packs = await packsTask;
                _managerModels = await mgrModelsTask;
                foreach (var n in _report.MissingNodes.Where(n => string.IsNullOrEmpty(n.RepoUrl)))
                {
                    if (!map.TryGetValue(n.ClassType, out var key)) continue;
                    // The map names GitHub packs by URL and registry packs by their id.
                    var pack = ComfyServerSetupClient.FindPack(_packs, key);
                    var repo = pack != null
                        ? (string.IsNullOrEmpty(pack.Repository) ? pack.Files.FirstOrDefault() ?? "" : pack.Repository)
                        : key.Contains("://") ? key : "";
                    n.RepoUrl = repo;
                    n.PackName = !string.IsNullOrEmpty(pack?.Title) ? pack!.Title
                               : repo.Length > 0 ? NodeCatalog.PackNameFromRepo(repo) : key;
                }
                Log($"{workflows.Count} workflows; {_report.MissingNodes.Count} node types and " +
                    $"{_report.MissingModels.Count(m => m.ServerMatch == null)} models missing; " +
                    $"{_report.MissingModels.Count(m => m.ServerMatch != null)} models found under another folder.");

                SetStatus("Finding the server's model folders ...");
                await LocateFoldersAsync(ct);

                SetStatus("Looking for the missing models in folders on this PC ...");
                var searchFolders = ModelSearchFolders();
                _localIndex = await Task.Run(() => ModelTransfer.IndexModelFiles(searchFolders, ct), ct);

                BuildRows();
                SetStatus(_report.HasUncheckedModels && _report.MissingNodes.Count > 0
                    ? "Some models sit on nodes that aren't installed yet; FlipPix checks them again after the node packs are installed."
                    : "Scan complete.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                SetStatus("Cancelled.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Server setup scan failed");
                Log("Scan failed: " + ex.Message);
                SetStatus("Couldn't check the server: " + ex.Message);
                ServerLine.Text = $"{_baseUrl} — not reachable. Is ComfyUI running and listening on the network (--listen)?";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task LocateFoldersAsync(CancellationToken ct)
        {
            var s = _settingsService.Settings;
            var hints = new List<string?> { s.RemoteModelsFolderPath, ParentOf(s.RemoteOutputFolderPath), ParentOf(s.RemoteLoraFolderPath) };
            hints.AddRange(_extraShareHints);
            _folders = await ModelShareLocator.LocateAsync(_baseUrl, _serverFolders, hints, ct);
            _writable.Clear();
            foreach (var (category, mapping) in _folders)
                _writable[category] = await Task.Run(() => ModelShareLocator.CanWrite(mapping.LocalPath), ct);

            var writableCount = _writable.Count(w => w.Value);
            if (_serverFolders.Count == 0)
            {
                FolderLine.Text = "The server doesn't list its model folders, so FlipPix can't put models there directly. " +
                                  "Use \"Set models folder…\" if you have its models folder shared, or copy the server commands.";
            }
            else if (writableCount == 0)
            {
                FolderLine.Text = _folders.Count == 0
                    ? "FlipPix can't reach the server's model folders from this PC. Share the server's models folder " +
                      "(or map it as a drive) and use \"Set models folder…\", or copy the server commands."
                    : "The server's model folders are reachable but read-only from this PC, so models can't be copied in.";
            }
            else
            {
                var root = CommonRoot(_folders.Where(f => _writable.GetValueOrDefault(f.Key)).Select(f => f.Value.LocalPath));
                FolderLine.Text = $"Models go straight into the server's folders through {root} " +
                                  $"({writableCount} model folder{(writableCount == 1 ? "" : "s")} reachable).";
                // Remember it, so the missing-model dialog used during renders can install there too.
                if (string.IsNullOrWhiteSpace(_settingsService.Settings.RemoteModelsFolderPath) && !string.IsNullOrEmpty(root)
                    && !ModelShareLocator.IsLocalHost(new Uri(_baseUrl).Host))
                {
                    _settingsService.Settings.RemoteModelsFolderPath = root;
                    _settingsService.SaveSettings(_settingsService.Settings);
                    Log($"Saved {root} as the remote models folder.");
                }
            }
            foreach (var (category, mapping) in _folders.OrderBy(f => f.Key))
                Log($"  {category}: {mapping.ServerPath} = {mapping.LocalPath}{(_writable.GetValueOrDefault(category) ? "" : " (read-only)")}");
        }

        private List<string> ModelSearchFolders()
        {
            var s = _settingsService.Settings;
            var list = new List<string>();
            if (s.UserModelSourceFolders != null) list.AddRange(s.UserModelSourceFolders);
            if (!string.IsNullOrWhiteSpace(s.ComfyUIFolderPath)) list.Add(Path.Combine(s.ComfyUIFolderPath, "models"));
            if (!string.IsNullOrWhiteSpace(s.WslModelsFolderPath)) list.Add(s.WslModelsFolderPath);
            // Don't count the server's own folders as "a copy you already have".
            var serverLocal = new HashSet<string>(_folders.Values.Select(f => f.LocalPath.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            return list.Where(f => !string.IsNullOrWhiteSpace(f) && !serverLocal.Contains(f.TrimEnd('\\')))
                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ================================================================ rows

        private void BuildRows()
        {
            if (_report == null || _info == null) return;

            _nodeRows.Clear();
            var groups = _report.MissingNodes
                .GroupBy(n => string.IsNullOrEmpty(n.RepoUrl) ? "class:" + n.ClassType : ComfyServerSetupClient.NormalizeRepo(n.RepoUrl));
            foreach (var g in groups.OrderByDescending(g => g.SelectMany(n => n.Workflows).Distinct().Count()))
            {
                var first = g.First();
                var workflows = g.SelectMany(n => n.Workflows).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(w => w).ToList();
                var row = new NodePackRow
                {
                    RepoUrl = first.RepoUrl,
                    PackName = string.IsNullOrEmpty(first.PackName) ? "(unknown pack)" : first.PackName,
                    Nodes = string.Join(", ", g.Select(n => n.ClassType)),
                    UsedBy = workflows.Count.ToString(),
                    WorkflowList = string.Join("\n", workflows.Take(40)) + (workflows.Count > 40 ? $"\n… and {workflows.Count - 40} more" : ""),
                };
                ClassifyPack(row);
                _nodeRows.Add(row);
            }

            _allModelRows.Clear();
            foreach (var m in _report.MissingModels)
            {
                var row = new ModelRow
                {
                    Model = m,
                    Name = m.Name,
                    Category = string.IsNullOrEmpty(m.Category) ? "?" : m.Category,
                    UsedBy = m.Workflows.Count.ToString(),
                    WorkflowList = string.Join("\n", m.Workflows.Take(40)) + (m.Workflows.Count > 40 ? $"\n… and {m.Workflows.Count - 40} more" : ""),
                };
                ClassifyModel(row);
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModelRow.Selected)) UpdateSelectedSize(); };
                _allModelRows.Add(row);
            }
            RefreshModelView();

            var r = _report;
            ReadyNumber.Text = $"{r.ReadyWorkflows} / {r.Workflows.Count}";
            NodesNumber.Text = _nodeRows.Count.ToString();
            ModelsNumber.Text = _allModelRows.Count(x => x.Kind != ModelSource.OnServer).ToString();
            RemappedNumber.Text = _allModelRows.Count(x => x.Kind == ModelSource.OnServer).ToString();

            WorkflowsList.ItemsSource = r.Workflows
                .OrderBy(w => w.Ready).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .Select(w => new { w.Name, Status = DescribeWorkflow(w) })
                .ToList();

            InstallButton.IsEnabled = _nodeRows.Any(n => n.CanInstall) || _allModelRows.Any(m => m.CanInstall);
            Tabs.SelectedItem = _nodeRows.Count > 0 ? NodesTab : ModelsTab;
        }

        private static string DescribeWorkflow(WorkflowReadiness w)
        {
            if (w.Ready)
                return w.RemappedModels > 0 ? $"Ready ({w.RemappedModels} model(s) from another folder)" : "Ready";
            var parts = new List<string>();
            if (w.MissingNodes > 0) parts.Add($"{w.MissingNodes} node type{(w.MissingNodes == 1 ? "" : "s")}");
            if (w.MissingModels > 0) parts.Add($"{w.MissingModels} model{(w.MissingModels == 1 ? "" : "s")}");
            return "Needs " + string.Join(" and ", parts);
        }

        private void ClassifyPack(NodePackRow row)
        {
            row.Pack = string.IsNullOrEmpty(row.RepoUrl) ? null : ComfyServerSetupClient.FindPackByRepo(_packs, row.RepoUrl);
            if (string.IsNullOrEmpty(row.RepoUrl))
            {
                row.Route = "No known pack";
                row.Kind = PackRoute.Unknown;
                row.Status = "Search ComfyUI-Manager for these node names.";
            }
            else if (_info!.Manager == ManagerApi.None)
            {
                row.Route = "Server command";
                row.Kind = PackRoute.Manual;
                row.Status = "ComfyUI-Manager isn't on the server; use \"Copy server commands\".";
            }
            else if (row.Pack == null)
            {
                row.Route = "Server command";
                row.Kind = PackRoute.Manual;
                row.Status = "ComfyUI-Manager doesn't list this pack; use \"Copy server commands\".";
            }
            else if (row.Pack.State is "enabled" or "import-fail")
            {
                row.Route = "Repair (Manager \"Try fix\")";
                row.Kind = PackRoute.Repair;
                row.Status = "Installed but its nodes don't load — usually a missing Python package.";
            }
            else if (row.Pack.State == "disabled")
            {
                row.Route = "Enable";
                row.Kind = PackRoute.Enable;
                row.Status = "Installed but switched off in Manager.";
            }
            else
            {
                row.Route = string.IsNullOrEmpty(row.Pack.CnrLatest) ? "Manager (GitHub)" : $"Manager (registry {row.Pack.CnrLatest})";
                row.Kind = PackRoute.Manager;
                row.Status = "Ready to install.";
            }
            row.CanInstall = row.Kind is PackRoute.Manager or PackRoute.Repair or PackRoute.Enable;
            row.Selected = row.CanInstall;
        }

        private void ClassifyModel(ModelRow row)
        {
            var m = row.Model!;
            row.Url = ModelCatalog.GetUrl(m.Name);
            row.ManagerModel = _managerModels.FirstOrDefault(x => string.Equals(x.Filename, m.FileName, StringComparison.OrdinalIgnoreCase));
            row.LocalFile = _localIndex.TryGetValue(m.FileName, out var files) ? files.OrderByDescending(f => f.Length).First() : null;
            row.TargetPath = TargetFor(m);

            long? bytes = row.LocalFile?.Length;
            row.Size = bytes.HasValue ? ModelTransfer.FormatBytes(bytes.Value)
                     : ModelCatalog.GetSizeHint(m.Name) ?? (string.IsNullOrEmpty(row.ManagerModel?.Size) ? "" : row.ManagerModel!.Size);
            row.SizeBytes = bytes ?? ParseSize(row.Size);

            if (m.ServerMatch != null)
            {
                row.Kind = ModelSource.OnServer;
                row.Source = "On the server";
                row.SourceDetail = $"The server has it as {m.ServerMatch}; FlipPix uses that copy automatically.";
                row.Status = "Nothing to do.";
            }
            else if (row.LocalFile != null && row.TargetPath != null)
            {
                row.Kind = ModelSource.LocalCopy;
                row.Source = "Copy from this PC";
                row.SourceDetail = $"{row.LocalFile.FullName}\n→ {row.TargetPath}";
                row.Status = "Ready to copy.";
            }
            else if (row.ManagerModel != null)
            {
                row.Kind = ModelSource.ManagerDownload;
                row.Source = "Server downloads it";
                row.SourceDetail = $"ComfyUI-Manager downloads it on the server into models/{row.ManagerModel.SavePath}.\n{row.ManagerModel.Url}";
                row.Status = "Ready to install.";
            }
            else if (row.Url != null && row.TargetPath != null)
            {
                row.Kind = ModelSource.Download;
                row.Source = "Download";
                row.SourceDetail = $"{row.Url}\n→ {row.TargetPath}";
                row.Status = "Ready to download.";
            }
            else if (row.Url != null)
            {
                row.Kind = ModelSource.ServerCommand;
                row.Source = "Server command";
                row.SourceDetail = $"{row.Url}\nThe server's {m.Category} folder isn't reachable from this PC; it's included in \"Copy server commands\".";
                row.Status = "Download it on the server.";
            }
            else
            {
                row.Kind = ModelSource.None;
                row.Source = row.LocalFile != null ? "Found here, no folder" : "No known source";
                row.SourceDetail = row.LocalFile != null
                    ? $"Found at {row.LocalFile.FullName}, but the server's {m.Category} folder isn't reachable from this PC."
                    : "FlipPix doesn't know where to download this. Use \"Find models in a folder…\" if you have it, or put it on the server yourself.";
                row.Status = string.IsNullOrEmpty(m.Category) ? "Folder unknown." : $"Goes in models/{m.Category}.";
            }

            row.CanInstall = row.Kind is ModelSource.LocalCopy or ModelSource.ManagerDownload or ModelSource.Download;
            // Copies are cheap; downloads can run to hundreds of GB across every workflow, so only the core
            // image models start ticked and the rest are one click away.
            row.Selected = row.Kind == ModelSource.LocalCopy
                           || (row.Kind is ModelSource.ManagerDownload or ModelSource.Download && _essentials.Contains(m.FileName));
        }

        private string? TargetFor(RequiredModel m)
        {
            if (string.IsNullOrEmpty(m.Category)) return null;
            if (!_folders.TryGetValue(m.Category, out var mapping) || !_writable.GetValueOrDefault(m.Category)) return null;
            var rel = m.Name.Replace('/', '\\').TrimStart('\\');
            return Path.Combine(mapping.LocalPath, rel);
        }

        private void RefreshModelView()
        {
            _modelRows.Clear();
            var showOnServer = ShowRemappedCheck.IsChecked == true;
            // What FlipPix can do something about first, then by how many workflows need it.
            foreach (var r in _allModelRows.Where(r => showOnServer || r.Kind != ModelSource.OnServer)
                         .OrderBy(r => r.Kind switch
                         {
                             ModelSource.LocalCopy => 0, ModelSource.ManagerDownload => 1, ModelSource.Download => 2,
                             ModelSource.ServerCommand => 3, ModelSource.None => 4, _ => 5,
                         })
                         .ThenByDescending(r => r.Model!.Workflows.Count))
                _modelRows.Add(r);
            UpdateSelectedSize();
        }

        private void UpdateSelectedSize()
        {
            var selected = _allModelRows.Where(r => r.Selected && r.CanInstall).ToList();
            var bytes = selected.Sum(r => Math.Max(0, r.SizeBytes));
            SelectedSizeText.Text = selected.Count == 0 ? "" : $"{selected.Count} selected, about {ModelTransfer.FormatBytes(bytes)}";
        }

        // ================================================================ install

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            var packs = _nodeRows.Where(r => r.Selected && r.CanInstall).ToList();
            var models = _allModelRows.Where(r => r.Selected && r.CanInstall).ToList();
            if (packs.Count == 0 && models.Count == 0)
            {
                SetStatus("Nothing selected.");
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetBusy(true, "Installing ...", cancellable: true);
            var log = new Progress<string>(Log);
            bool nodesChanged = false;

            try
            {
                // 1) Everything ComfyUI-Manager does on the server goes into one queue run.
                int queued = 0;
                foreach (var p in packs)
                {
                    var (result, message) = p.Kind == PackRoute.Repair
                        ? await _client.QueuePackFixAsync(p.Pack!, ct)
                        : await _client.QueuePackInstallAsync(p.Pack!, ct);
                    p.Status = result == QueueResult.Queued ? "Queued ..." : $"Refused: {message}";
                    Log($"{p.PackName}: {(result == QueueResult.Queued ? "queued" : message)}");
                    if (result == QueueResult.Queued) queued++;
                    else if (result == QueueResult.Forbidden) p.Kind = PackRoute.Manual;
                }
                foreach (var m in models.Where(m => m.Kind == ModelSource.ManagerDownload))
                {
                    var (result, message) = await _client.QueueModelInstallAsync(m.ManagerModel!, ct);
                    m.Status = result == QueueResult.Queued ? "Queued on the server ..." : $"Refused: {message}";
                    Log($"{m.Model!.FileName}: {(result == QueueResult.Queued ? "queued on the server" : message)}");
                    if (result == QueueResult.Queued) queued++;
                }
                if (queued > 0)
                {
                    SetStatus($"ComfyUI-Manager is installing {queued} item(s) on the server ...");
                    await _client.StartQueueAsync(ct);
                    await _client.WaitForQueueAsync(log, TimeSpan.FromMinutes(45), ct);

                    // Manager accepts a request and only refuses it when the task runs (a flagged registry
                    // version, its security level ...), so read back what actually changed.
                    var after = await _client.GetPackListAsync(ct);
                    foreach (var p in packs.Where(p => p.Status.StartsWith("Queued")))
                    {
                        var now = ComfyServerSetupClient.FindPack(after, p.Pack!.Id) ?? ComfyServerSetupClient.FindPackByRepo(after, p.RepoUrl);
                        bool changed = p.Kind == PackRoute.Repair
                                       || (now != null && now.State != p.Pack.State && now.State != "not-installed");
                        if (changed)
                        {
                            p.Status = p.Kind == PackRoute.Repair ? "Repaired; checked after the restart." : "Installed.";
                            nodesChanged = true;
                        }
                        else
                        {
                            p.Status = "ComfyUI-Manager didn't install it. Its security settings block this pack " +
                                       "(e.g. a flagged registry version); the reason is in ComfyUI's console.";
                            p.Kind = PackRoute.Manual;
                            Log($"{p.PackName}: not installed — ComfyUI-Manager refused it while running the queue. " +
                                "If you trust it, install it on the server with \"Copy server commands\", or allow it in " +
                                "ComfyUI-Manager's config.ini (allow_flagged_nodepack_install for flagged registry versions).");
                        }
                    }
                    foreach (var m in models.Where(m => m.Status.StartsWith("Queued")))
                    {
                        // A Manager download lands where the server lists it; check it's there now.
                        m.Status = "Downloaded on the server (checked on the rescan).";
                    }
                }

                // 2) Copies and downloads into the server's folders from this PC, one at a time.
                var transfers = models.Where(m => m.Kind is ModelSource.LocalCopy or ModelSource.Download).ToList();
                for (int i = 0; i < transfers.Count; i++)
                {
                    var m = transfers[i];
                    var verb = m.Kind == ModelSource.LocalCopy ? "Copying" : "Downloading";
                    var label = $"{verb} {m.Model!.FileName} ({i + 1}/{transfers.Count})";
                    SetStatus(label);
                    Progress.IsIndeterminate = false;
                    var progress = new Progress<(long done, long total)>(p =>
                    {
                        Progress.Value = p.total > 0 ? 100.0 * p.done / p.total : 0;
                        var text = p.total > 0
                            ? $"{ModelTransfer.FormatBytes(p.done)} of {ModelTransfer.FormatBytes(p.total)}"
                            : ModelTransfer.FormatBytes(p.done);
                        m.Status = text;
                        StatusText.Text = $"{label} — {text}";
                    });
                    try
                    {
                        if (m.Kind == ModelSource.LocalCopy)
                            await ModelTransfer.CopyAsync(m.LocalFile!.FullName, m.TargetPath!, progress, ct);
                        else
                            await ModelTransfer.DownloadAsync(m.Url!, m.TargetPath!, progress, ct);
                        m.Status = m.Kind == ModelSource.LocalCopy ? "Copied." : "Downloaded.";
                        Log($"{m.Model.FileName}: {m.Status.TrimEnd('.').ToLowerInvariant()} to {m.TargetPath}");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        m.Status = "Failed: " + ex.Message;
                        Log($"{m.Model.FileName}: failed — {ex.Message}");
                        _logger?.LogError(ex, $"Server setup transfer of {m.Model.FileName} failed");
                    }
                    finally
                    {
                        Progress.IsIndeterminate = true;
                    }
                }

                // 3) New node packs only load on a restart; models are picked up without one.
                if (!nodesChanged && packs.Count > 0)
                    Log("No node pack changed, so ComfyUI isn't restarted.");
                if (nodesChanged)
                {
                    SetStatus("Restarting ComfyUI so the new nodes load ...");
                    var ok = await _client.RebootAsync(log, ct);
                    if (!ok) SetStatus("ComfyUI didn't come back yet. Restart it on the server, then click Rescan.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log("Cancelled. A partly downloaded model resumes from where it stopped next time.");
                SetBusy(false);
                SetStatus("Cancelled.");
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Server setup install failed");
                Log("Install failed: " + ex.Message);
            }

            SetBusy(false);
            Log("Scanning again ...");
            await ScanAsync();
        }

        // ================================================================ buttons

        private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (CloseButton.Content as string == "Cancel")
            {
                _cts?.Cancel();
                return;
            }
            Close();
        }

        private async void ScanFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = PickFolder("Pick a folder that has ComfyUI models in it (an old install, a drive, a NAS ...)");
            if (folder == null) return;

            // Remember it for the missing-model dialog and later scans.
            var s = _settingsService.Settings;
            s.UserModelSourceFolders ??= new List<string>();
            s.UserModelSourceFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            s.UserModelSourceFolders.Insert(0, folder);
            if (s.UserModelSourceFolders.Count > 10) s.UserModelSourceFolders = s.UserModelSourceFolders.Take(10).ToList();
            _settingsService.SaveSettings(s);

            SetBusy(true, $"Looking for models in {folder} ...");
            try
            {
                var folders = ModelSearchFolders();
                _localIndex = await Task.Run(() => ModelTransfer.IndexModelFiles(folders, CancellationToken.None));
                var before = _allModelRows.Count(r => r.Kind == ModelSource.LocalCopy);
                foreach (var r in _allModelRows) ClassifyModel(r);
                RefreshModelView();
                var found = _allModelRows.Count(r => r.Kind == ModelSource.LocalCopy) - before;
                SetStatus(found > 0
                    ? $"Found {found} missing model(s) in {folder}. They are selected to be copied."
                    : $"None of the missing models are in {folder}.");
                InstallButton.IsEnabled = _nodeRows.Any(n => n.CanInstall) || _allModelRows.Any(m => m.CanInstall);
                Tabs.SelectedItem = ModelsTab;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void PickShare_Click(object sender, RoutedEventArgs e)
        {
            var folder = PickFolder("Pick the server's ComfyUI models folder as this PC sees it (a network share or mapped drive)");
            if (folder == null) return;
            _extraShareHints.Insert(0, folder);
            if (!ModelShareLocator.IsLocalHost(new Uri(_baseUrl).Host))
            {
                _settingsService.Settings.RemoteModelsFolderPath = folder;
                _settingsService.SaveSettings(_settingsService.Settings);
            }
            SetBusy(true, "Matching the folder with the server's model folders ...");
            try
            {
                await LocateFoldersAsync(CancellationToken.None);
                if (_report != null)
                {
                    foreach (var r in _allModelRows) ClassifyModel(r);
                    RefreshModelView();
                }
                if (!_folders.Values.Any(f => f.LocalPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                    SetStatus($"{folder} doesn't line up with any model folder the server reports. Pick the folder that holds " +
                              "\"checkpoints\", \"loras\", \"vae\" ... on the server.");
                else
                    SetStatus("Models folder set.");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void CopyScript_Click(object sender, RoutedEventArgs e)
        {
            if (_info == null) return;
            var repos = _nodeRows.Where(r => r.Kind == PackRoute.Manual && !string.IsNullOrEmpty(r.RepoUrl))
                                 .Select(r => r.RepoUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var models = new List<ScriptModel>();
            foreach (var r in _allModelRows.Where(r => r.Kind is ModelSource.ServerCommand or ModelSource.Download && r.Url != null))
            {
                var category = r.Model!.Category;
                if (string.IsNullOrEmpty(category) || !_serverFolders.TryGetValue(category, out var dirs) || dirs.Count == 0) continue;
                models.Add(new ScriptModel(PreferredServerFolder(category, dirs), r.Model.Name, r.Url!));
            }
            if (repos.Count == 0 && models.Count == 0)
            {
                SetStatus("Nothing needs doing on the server by hand.");
                return;
            }
            var customNodes = _serverFolders.TryGetValue("custom_nodes", out var cn) && cn.Count > 0 ? cn[0] : null;
            var script = ServerSetupScript.Build(_info.IsWindows, _baseUrl, customNodes, repos, models);
            try
            {
                System.Windows.Clipboard.SetText(script);
                SetStatus($"Copied a script with {repos.Count} node pack(s) and {models.Count} model download(s). " +
                          "Paste it into a terminal on the server, then click Rescan.");
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't use the clipboard: " + ex.Message);
            }
            Log("---- server commands ----\n" + script);
        }

        // Of a category's server folders, the one this PC also reaches (so files land where FlipPix can see
        // them later), else the first the server lists.
        private string PreferredServerFolder(string category, List<string> dirs) =>
            _folders.TryGetValue(category, out var m) ? m.ServerPath : dirs[0];

        private void SelectAllNodes_Click(object sender, RoutedEventArgs e) { foreach (var r in _nodeRows) r.Selected = r.CanInstall; }
        private void SelectNoNodes_Click(object sender, RoutedEventArgs e) { foreach (var r in _nodeRows) r.Selected = false; }
        private void SelectAllModels_Click(object sender, RoutedEventArgs e) { foreach (var r in _modelRows) r.Selected = r.CanInstall; }
        private void SelectNoModels_Click(object sender, RoutedEventArgs e) { foreach (var r in _allModelRows) r.Selected = false; }

        private void SelectEssentialModels_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in _allModelRows)
                r.Selected = r.CanInstall && (r.Kind == ModelSource.LocalCopy || _essentials.Contains(r.Model!.FileName));
        }

        private void ShowRemapped_Changed(object sender, RoutedEventArgs e) => RefreshModelView();

        // ================================================================ helpers

        private void SetBusy(bool busy, string? status = null, bool cancellable = false)
        {
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Progress.IsIndeterminate = true;
            RescanButton.IsEnabled = !busy;
            ScriptButton.IsEnabled = !busy;
            ScanFolderButton.IsEnabled = !busy;
            PickShareButton.IsEnabled = !busy;
            InstallButton.IsEnabled = !busy && (_nodeRows.Any(n => n.CanInstall) || _allModelRows.Any(m => m.CanInstall));
            CloseButton.Content = busy && cancellable ? "Cancel" : "Close";
            if (busy && cancellable) InstallButton.IsEnabled = false;
            if (status != null) SetStatus(status);
        }

        private void SetStatus(string text) => StatusText.Text = text;

        private void Log(string line)
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
            LogBox.ScrollToEnd();
            _logger?.LogInfo("Server setup: " + line);
        }

        private static string DescribeServer(ServerInfo i)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(i.ComfyVersion)) parts.Add($"ComfyUI {i.ComfyVersion}");
            if (!string.IsNullOrEmpty(i.Os)) parts.Add(i.IsWindows ? "Windows" : i.Os == "posix" || i.Os == "linux" ? "Linux" : i.Os);
            if (!string.IsNullOrEmpty(i.GpuName)) parts.Add(i.VramGb > 0 ? $"{CleanGpu(i.GpuName)} ({i.VramGb:0} GB)" : CleanGpu(i.GpuName));
            parts.Add(i.Manager == ManagerApi.None ? "no ComfyUI-Manager" : $"ComfyUI-Manager {i.ManagerVersion}");
            return string.Join(" · ", parts);

            static string CleanGpu(string name)
            {
                // "cuda:0 NVIDIA GeForce RTX 4090 : cudaMallocAsync" -> "NVIDIA GeForce RTX 4090"
                var s = name;
                var colon = s.IndexOf(' ');
                if (s.StartsWith("cuda:") && colon > 0) s = s[(colon + 1)..];
                var tail = s.IndexOf(" : ", StringComparison.Ordinal);
                return tail > 0 ? s[..tail] : s;
            }
        }

        private static string? ParentOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetDirectoryName(path.TrimEnd('\\', '/')); } catch { return null; }
        }

        private static string CommonRoot(IEnumerable<string> paths)
        {
            var list = paths.Select(p => p.TrimEnd('\\')).ToList();
            if (list.Count == 0) return "";
            if (list.Count == 1) return Path.GetDirectoryName(list[0]) ?? list[0];
            var prefix = list[0];
            foreach (var p in list.Skip(1))
                // Drive roots keep their backslash ("Z:\"), so compare with exactly one trailing separator.
                while (prefix.Length > 0 && !(p + "\\").StartsWith(prefix.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    prefix = Path.GetDirectoryName(prefix.TrimEnd('\\')) is { Length: > 0 } parent && parent != prefix ? parent : "";
            return prefix.Length > 0 ? prefix : list[0];
        }

        private static long ParseSize(string? hint)
        {
            if (string.IsNullOrWhiteSpace(hint)) return -1;
            var s = hint.Trim().TrimStart('~').Trim();
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string num = parts[0], unit = parts.Length > 1 ? parts[1] : "";
            if (parts.Length == 1)
            {
                int i = 0;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                num = s[..i];
                unit = s[i..];
            }
            if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return -1;
            return unit.ToUpperInvariant() switch
            {
                "TB" => (long)(v * 1024 * 1024 * 1024 * 1024),
                "GB" => (long)(v * 1024 * 1024 * 1024),
                "MB" => (long)(v * 1024 * 1024),
                "KB" => (long)(v * 1024),
                _ => -1,
            };
        }

        private static HashSet<string> LoadEssentials()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                var file = Path.Combine(baseDir, "scripts", "flippix-models-min.txt");
                if (!File.Exists(file)) return set;
                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var rel = line.Split('|')[0].Trim();
                    if (rel.Length > 0) set.Add(Path.GetFileName(rel.Replace('\\', '/')));
                }
            }
            catch { /* optional */ }
            return set;
        }

        private string? PickFolder(string description)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
        }

        // ================================================================ row models

        private enum PackRoute { Manager, Repair, Enable, Manual, Unknown }

        private enum ModelSource { OnServer, LocalCopy, ManagerDownload, Download, ServerCommand, None }

        private abstract class Row : INotifyPropertyChanged
        {
            private bool _selected;
            private string _status = "";

            public bool CanInstall { get; set; }
            public string UsedBy { get; set; } = "";
            public string WorkflowList { get; set; } = "";

            public bool Selected
            {
                get => _selected;
                set { if (_selected != value) { _selected = value; OnChanged(); } }
            }

            public string Status
            {
                get => _status;
                set { if (_status != value) { _status = value; OnChanged(); } }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            protected void OnChanged([CallerMemberName] string? name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private sealed class NodePackRow : Row
        {
            public string PackName { get; set; } = "";
            public string RepoUrl { get; set; } = "";
            public string Nodes { get; set; } = "";
            public string Route { get; set; } = "";
            public PackRoute Kind { get; set; }
            public ManagerPack? Pack { get; set; }
        }

        private sealed class ModelRow : Row
        {
            private string _source = "";

            public RequiredModel? Model { get; set; }
            public string Name { get; set; } = "";
            public string Category { get; set; } = "";
            public string Size { get; set; } = "";
            public long SizeBytes { get; set; }
            public string SourceDetail { get; set; } = "";
            public ModelSource Kind { get; set; }
            public string? Url { get; set; }
            public ManagerModel? ManagerModel { get; set; }
            public FileInfo? LocalFile { get; set; }
            public string? TargetPath { get; set; }

            public string Source
            {
                get => _source;
                set { if (_source != value) { _source = value; OnChanged(); } }
            }
        }
    }
}
