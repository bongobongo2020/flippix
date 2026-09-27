using System.Text.Json;
using FlipPix.Remote.Contracts;
using FlipPix.Remote.Engine;
using FlipPix.Remote.Library;

namespace FlipPix.Remote.Jobs;

/// <summary>
/// The phone's jobs, run one at a time on the desktop in the order they were asked for.
///
/// <para>One at a time on purpose: every job ends on the one GPU, and a queue that is visible on the
/// phone ("2nd in line") is better than two jobs fighting inside ComfyUI. The phone can close, sleep or
/// lose Wi-Fi; the job carries on here and the phone catches up from <see cref="WaitAsync"/>.</para>
///
/// <para>Every change bumps <see cref="Revision"/>. A phone asks for "anything newer than N" and the
/// request is held until there is (a long poll), so progress arrives as it happens without a socket
/// that a sleeping phone would drop.</para>
/// </summary>
public sealed class JobManager : IDisposable
{
    private const int KeepJobs = 80;
    private const int KeepMade = 3000;

    private readonly RemoteEngine _engine;
    private readonly string _jobsFile;
    private readonly string _madeFile;
    private readonly object _lock = new();
    private readonly List<RemoteJob> _jobs = new();          // newest first
    private readonly Dictionary<string, MadeRecord> _made = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _madeOrder = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _current;
    private string? _currentId;
    private long _revision = 1;
    private Task? _worker;

    public JobManager(RemoteEngine engine)
    {
        _engine = engine;
        _jobsFile = Path.Combine(engine.DataDir, "jobs.json");
        _madeFile = Path.Combine(engine.DataDir, "made.json");
    }

    /// <summary>Raised after any change, on a worker thread. The desktop window counts jobs from it.</summary>
    public event Action? Changed;

    public long Revision { get { lock (_lock) return _revision; } }

    public (int Queued, int Running) Counts()
    {
        lock (_lock)
            return (_jobs.Count(j => j.State == JobStates.Queued), _jobs.Count(j => j.State == JobStates.Running));
    }

    // ── Lifetime ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads the saved jobs (off the UI thread) and starts the worker.</summary>
    public async Task StartAsync()
    {
        await Task.Run(Load);
        _worker = Task.Run(WorkAsync);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_jobsFile))
            {
                var saved = JsonSerializer.Deserialize<List<RemoteJob>>(File.ReadAllText(_jobsFile)) ?? new();
                foreach (var job in saved)
                {
                    // Nothing survives a restart mid-run: the render may still have landed in the output
                    // folder, but this process can't collect it. Say so, and let it be retried.
                    if (job.IsActive)
                    {
                        job.State = JobStates.Stopped;
                        job.Status = "The desktop closed before this finished. Its result may still be in the Library.";
                        job.Finished ??= DateTimeOffset.Now;
                        foreach (var item in job.Items.Where(i => i.State == ItemStates.Working))
                        {
                            item.State = ItemStates.Failed;
                            item.Status = "Interrupted";
                        }
                    }
                }
                lock (_lock) _jobs.AddRange(saved.OrderByDescending(j => j.Created));
            }
            if (File.Exists(_madeFile))
            {
                var made = JsonSerializer.Deserialize<List<KeyValuePair<string, MadeRecord>>>(File.ReadAllText(_madeFile)) ?? new();
                lock (_lock)
                    foreach (var (k, v) in made)
                    {
                        if (_made.TryAdd(k, v)) _madeOrder.Add(k);
                    }
            }
        }
        catch (Exception ex)
        {
            // A damaged file loses the history, never the ability to take new jobs.
            _engine.Logger.LogWarning("Couldn't read the saved phone jobs: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _current?.Cancel();
    }

    // ── What the phone asks for ────────────────────────────────────────────────────────────────

    public JobsPage Snapshot()
    {
        lock (_lock)
        {
            var queued = _jobs.Where(j => j.State == JobStates.Queued).OrderBy(j => j.Created).Select(j => j.Id).ToList();
            return new JobsPage
            {
                Revision = _revision,
                Jobs = _jobs.Select(j => ToDto(j, queued.IndexOf(j.Id) + 1)).ToList(),
            };
        }
    }

    public JobDto? Get(string id)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job == null) return null;
            var queued = _jobs.Where(j => j.State == JobStates.Queued).OrderBy(j => j.Created).Select(j => j.Id).ToList();
            return ToDto(job, queued.IndexOf(id) + 1);
        }
    }

    /// <summary>Returns once the revision passes <paramref name="since"/> or <paramref name="wait"/> runs out.</summary>
    public async Task<JobsPage> WaitAsync(long since, TimeSpan wait, CancellationToken ct)
    {
        Task changed;
        lock (_lock)
        {
            if (_revision > since) return SnapshotLocked();
            changed = _changed.Task;
        }
        await Task.WhenAny(changed, Task.Delay(wait, ct));
        ct.ThrowIfCancellationRequested();
        // Sampler steps arrive in bursts; a short pause folds a burst into one answer.
        if (changed.IsCompleted) await Task.Delay(200, ct);
        return Snapshot();
    }

    private JobsPage SnapshotLocked()
    {
        var queued = _jobs.Where(j => j.State == JobStates.Queued).OrderBy(j => j.Created).Select(j => j.Id).ToList();
        return new JobsPage { Revision = _revision, Jobs = _jobs.Select(j => ToDto(j, queued.IndexOf(j.Id) + 1)).ToList() };
    }

    /// <summary>Checks a request, fills in what can be known up front (title, shape), and queues it.</summary>
    public async Task<JobDto> EnqueueAsync(JobRequest request, CancellationToken ct)
    {
        var job = new RemoteJob { Request = request };
        switch (request.Kind)
        {
            case JobKinds.Image:
                if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Describe the picture first.");
                if (ImageLook.All.All(l => l.Key != request.Look)) throw new ArgumentException("Unknown look: " + request.Look);
                request.Prompt = request.Prompt.Trim();
                request.Count = Math.Clamp(request.Count, 1, 4);
                job.Title = TitleOf(request.Prompt, 90);
                job.Ratio = ImageRunner.Ratio(ImageRunner.ShapeOf(request.Shape));
                for (var i = 0; i < request.Count; i++)
                    job.Items.Add(new RemoteJobItem { Index = i, MediaKind = MediaKinds.Image, Status = "Waiting its turn" });
                break;

            case JobKinds.Video:
                if (request.Pictures.Count == 0) throw new ArgumentException("Add at least one photo.");
                request.Pictures = request.Pictures.Take(VideoRecipe.MaxReferences).ToList();
                request.Seconds = Math.Clamp(request.Seconds, 1, VideoRecipe.MaxSeconds);
                request.Idea = request.Idea?.Trim();
                var first = await _engine.Uploads.ResolveAsync(request.Pictures[0], ct);
                job.Aspect = VideoRecipe.AspectFor(first.Width, first.Height);
                job.Ratio = VideoRecipe.AspectRatioOf(job.Aspect);
                job.PosterRef = request.Pictures[0];
                job.Script = string.IsNullOrWhiteSpace(request.Script) ? null : request.Script.Trim();
                job.Title = string.IsNullOrWhiteSpace(request.Idea)
                    ? $"{request.Seconds} s from {request.Pictures.Count} photo{(request.Pictures.Count == 1 ? "" : "s")}"
                    : TitleOf(request.Idea, 90);
                job.Items.Add(new RemoteJobItem { Index = 0, MediaKind = MediaKinds.Video, Status = "Waiting its turn" });
                break;

            case JobKinds.Story:
                if (string.IsNullOrWhiteSpace(request.Story)) throw new ArgumentException("Write the story first.");
                if (!_engine.Llm.Target.IsSet)
                    throw new ArgumentException("A story needs a writing assistant. Set an LLM server in FlipPix's Settings on the desktop.");
                request.Story = request.Story.Trim();
                request.Clips = request.Clips is 3 or 6 or 12 ? request.Clips : 6;
                request.Pictures = request.Pictures.Take(StoryRecipe.MaxCast).ToList();
                foreach (var p in request.Pictures) await _engine.Uploads.ResolveAsync(p, ct); // fail now, not in five minutes
                job.Aspect = "16:9 (Widescreen)";
                if (request.Pictures.Count > 0)
                {
                    var lead = await _engine.Uploads.ResolveAsync(request.Pictures[0], ct);
                    job.Aspect = VideoRecipe.AspectFor(lead.Width, lead.Height);
                    job.PosterRef = request.Pictures[0];
                }
                job.Ratio = VideoRecipe.AspectRatioOf(job.Aspect);
                job.Title = StoryRunner.TitleOf(request.Story);
                break;

            default:
                throw new ArgumentException("Unknown job kind: " + request.Kind);
        }

        lock (_lock)
        {
            _jobs.Insert(0, job);
            TrimLocked();
        }
        Touch(persist: true);
        _wake.Release();
        return Get(job.Id)!;
    }

    public bool Cancel(string id)
    {
        CancellationTokenSource? running = null;
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job == null || !job.IsActive) return false;
            if (job.State == JobStates.Queued)
            {
                job.State = JobStates.Stopped;
                job.Status = "Cancelled before it started";
                job.Finished = DateTimeOffset.Now;
            }
            else if (_currentId == id)
            {
                running = _current;
                job.Status = "Stopping";
            }
        }
        running?.Cancel();
        Touch(persist: true);
        return true;
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job == null || job.IsActive) return false;
            _jobs.Remove(job);
        }
        Touch(persist: true);
        return true;
    }

    /// <summary>Queues a finished job again for whatever it didn't make; what was written is reused.</summary>
    public JobDto? Retry(string id)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job == null || !job.CanRetry) return null;
            job.State = JobStates.Queued;
            job.Status = "Waiting its turn";
            job.Created = DateTimeOffset.Now; // back of the queue, and back to the top of the list
            job.Started = null;
            job.Finished = null;
            foreach (var item in job.Items.Where(i => i.State != ItemStates.Done))
            {
                item.State = ItemStates.Waiting;
                item.Status = "Waiting its turn";
                item.Progress = 0;
            }
            _jobs.Remove(job);
            _jobs.Insert(0, job);
        }
        Touch(persist: true);
        _wake.Release();
        return Get(id);
    }

    public MadeRecord? MadeFor(string relativePath)
    {
        lock (_lock) return _made.GetValueOrDefault(relativePath.Replace('\\', '/'));
    }

    public RemoteJobItem? Item(string id, int index, out RemoteJob? job)
    {
        lock (_lock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
            return job?.Items.FirstOrDefault(i => i.Index == index);
        }
    }

    // ── The worker ─────────────────────────────────────────────────────────────────────────────

    private async Task WorkAsync()
    {
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            RemoteJob? job;
            lock (_lock) job = _jobs.Where(j => j.State == JobStates.Queued).MinBy(j => j.Created);
            if (job == null)
            {
                try { await _wake.WaitAsync(stop); } catch (OperationCanceledException) { return; }
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_lock)
            {
                if (job.State != JobStates.Queued) continue; // cancelled while we looked
                _current = cts;
                _currentId = job.Id;
                job.State = JobStates.Running;
                job.Started = DateTimeOffset.Now;
                job.Status = "Starting";
                job.Progress = 0;
            }
            Touch(persist: true);

            var context = new JobContext(this, _engine, job, cts.Token);
            try
            {
                _engine.SyncComfy();
                await (job.Request.Kind switch
                {
                    JobKinds.Image => ImageRunner.RunAsync(context),
                    JobKinds.Video => VideoRunner.RunAsync(context),
                    JobKinds.Story => StoryRunner.RunAsync(context),
                    _ => throw new InvalidOperationException("Unknown job kind."),
                });
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                Update(job, j =>
                {
                    j.State = JobStates.Stopped;
                    var done = j.Items.Count(i => i.State == ItemStates.Done);
                    j.Status = done > 0 ? $"Stopped after {done} of {j.Items.Count}" : "Stopped";
                    foreach (var item in j.Items.Where(i => i.State == ItemStates.Working))
                    {
                        item.State = ItemStates.Waiting;
                        item.Status = "Stopped";
                        item.Progress = 0;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                return; // the desktop is closing; Load() explains it next time
            }
            catch (Exception ex)
            {
                _engine.Logger.LogError(ex, "Phone job {Id} failed", job.Id);
                Update(job, j =>
                {
                    j.State = JobStates.Failed;
                    j.Status = FirstLine(ex.Message);
                    foreach (var item in j.Items.Where(i => i.State == ItemStates.Working))
                    {
                        item.State = ItemStates.Failed;
                        item.Status = "Not made";
                    }
                });
            }
            finally
            {
                lock (_lock)
                {
                    job.Finished = DateTimeOffset.Now;
                    _current = null;
                    _currentId = null;
                }
                _engine.Library.Invalidate();
                Touch(persist: true);
            }
        }
    }

    // ── Changes ────────────────────────────────────────────────────────────────────────────────

    internal void Update(RemoteJob job, Action<RemoteJob> change, bool persist = false)
    {
        lock (_lock) change(job);
        Touch(persist);
    }

    internal void Remember(string? relativePath, MadeRecord record)
    {
        if (relativePath == null) return;
        lock (_lock)
        {
            if (!_made.ContainsKey(relativePath)) _madeOrder.Add(relativePath);
            _made[relativePath] = record;
            while (_madeOrder.Count > KeepMade)
            {
                _made.Remove(_madeOrder[0]);
                _madeOrder.RemoveAt(0);
            }
        }
    }

    private void Touch(bool persist)
    {
        TaskCompletionSource fire;
        lock (_lock)
        {
            _revision++;
            fire = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        fire.TrySetResult();
        if (persist) _ = SaveAsync();
        try { Changed?.Invoke(); } catch (Exception) { /* a listener's problem, not the job's */ }
    }

    private void TrimLocked()
    {
        while (_jobs.Count > KeepJobs)
        {
            var oldest = _jobs.LastOrDefault(j => !j.IsActive);
            if (oldest == null) break;
            _jobs.Remove(oldest);
        }
    }

    private async Task SaveAsync()
    {
        string jobs, made;
        lock (_lock)
        {
            jobs = JsonSerializer.Serialize(_jobs);
            made = JsonSerializer.Serialize(_madeOrder.Select(k => new KeyValuePair<string, MadeRecord>(k, _made[k])).ToList());
        }
        await _saveGate.WaitAsync();
        try
        {
            await WriteAtomicAsync(_jobsFile, jobs);
            await WriteAtomicAsync(_madeFile, made);
        }
        catch (Exception ex)
        {
            _engine.Logger.LogWarning("Couldn't save the phone jobs: " + ex.Message);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static async Task WriteAtomicAsync(string path, string text)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    // ── The phone's view of a job ──────────────────────────────────────────────────────────────

    private static JobDto ToDto(RemoteJob j, int queuePosition)
    {
        var root = RemoteApi.Root;
        return new JobDto
        {
            Id = j.Id,
            Kind = j.Request.Kind,
            State = j.State,
            Title = j.Title,
            Status = j.State == JobStates.Queued
                ? (queuePosition <= 1 ? "Next in line" : $"{Ordinal(queuePosition)} in line")
                : j.Status,
            Progress = j.Progress,
            Created = j.Created,
            Started = j.Started,
            Finished = j.Finished,
            QueuePosition = queuePosition,
            Ratio = j.Ratio,
            Request = j.Request,
            Script = j.Script,
            PosterUrl = PictureThumbUrl(j.PosterRef),
            CanRetry = j.CanRetry,
            Items = j.Items.Select(i => new JobItemDto
            {
                Index = i.Index,
                State = i.State,
                Status = i.Status,
                Progress = i.Progress,
                Label = i.Label,
                Text = i.Text,
                MediaKind = i.MediaKind,
                FileUrl = i.Output == null ? null : $"{root}/jobs/{j.Id}/items/{i.Index}/file",
                ThumbUrl = i.Output == null ? null : $"{root}/jobs/{j.Id}/items/{i.Index}/thumb",
                PreviewUrl = i.Output == null || i.MediaKind != MediaKinds.Image ? null : $"{root}/jobs/{j.Id}/items/{i.Index}/preview",
                LibraryId = i.RelativePath is { } rel ? LibraryIndex.IdOf(rel) : null,
            }).ToList(),
        };
    }

    /// <summary>The thumbnail route for a picture reference: an upload's or a library item's.</summary>
    public static string? PictureThumbUrl(string? reference) => reference switch
    {
        null => null,
        _ when reference.StartsWith("upload:", StringComparison.Ordinal) => $"{RemoteApi.Root}/uploads/{reference[7..]}/thumb",
        _ when reference.StartsWith("library:", StringComparison.Ordinal) => $"{RemoteApi.Root}/library/{reference[8..]}/thumb",
        _ => null,
    };

    private static string Ordinal(int n) => n switch { 2 => "2nd", 3 => "3rd", _ => $"{n}th" };

    internal static string TitleOf(string text, int max)
    {
        var s = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    internal static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? s;
        return line.Length > 180 ? line[..180] + "…" : line;
    }
}

/// <summary>What a runner gets: its job, the engine, and safe ways to change what the phone sees.</summary>
public sealed class JobContext
{
    private readonly JobManager _manager;

    internal JobContext(JobManager manager, RemoteEngine engine, RemoteJob job, CancellationToken ct)
    {
        _manager = manager;
        Engine = engine;
        Job = job;
        Token = ct;
    }

    public RemoteEngine Engine { get; }
    public RemoteJob Job { get; }
    public CancellationToken Token { get; }

    public void Set(Action<RemoteJob> change, bool persist = false) => _manager.Update(Job, change, persist);

    public void SetItem(RemoteJobItem item, Action<RemoteJobItem> change, bool persist = false) =>
        _manager.Update(Job, _ => change(item), persist);

    /// <summary>Records how a finished item was made, and starts its thumbnail so the phone's tile fills at once.</summary>
    public void Remember(RemoteJobItem item, MadeRecord record)
    {
        _manager.Remember(item.RelativePath, record);
        if (item.RelativePath is { } rel) Engine.Library.Include(rel);
        Engine.Warm(item);
    }

    /// <summary>
    /// Sampler progress for an item that is still being made. ComfyUI's progress reaches here through
    /// Progress&lt;T&gt;, which posts to the thread pool, so a late step can arrive after the item was
    /// marked done; it is dropped rather than turning a finished tile back into a working one.
    /// </summary>
    public bool Report(RemoteJobItem item, double progress, string status)
    {
        var applied = false;
        _manager.Update(Job, _ =>
        {
            if (item.State != Contracts.ItemStates.Working) return;
            item.Progress = progress;
            item.Status = status;
            applied = true;
        });
        return applied;
    }
}
