using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.Mobile.ViewModels;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.Services;

/// <summary>
/// The phone's live copy of the computer's job list. One long poll at a time: the computer holds the
/// request until something changes, so progress arrives as it happens, and a phone that slept or lost
/// Wi-Fi simply asks again and catches up. Every page reads its own kind of job from <see cref="Jobs"/>.
/// </summary>
public sealed partial class JobsHub : ObservableObject
{
    private readonly RemoteClient _remote;
    // Made on first Start, never in the constructor: AppServices builds this hub in a static
    // constructor, and a DispatcherTimer made before Avalonia starts breaks its dispatcher.
    private DispatcherTimer? _clock;
    private CancellationTokenSource? _loop;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public JobsHub(RemoteClient remote)
    {
        _remote = remote;
    }

    /// <summary>Newest first, as the computer lists them.</summary>
    public ObservableCollection<JobVm> Jobs { get; } = new();

    /// <summary>Raised on the UI thread after each update, for pages that derive lists from <see cref="Jobs"/>.</summary>
    public event Action? Changed;

    [ObservableProperty] private bool _isOnline;

    /// <summary>Starts (or restarts, after pairing with another computer) following the job list.</summary>
    public void Start()
    {
        _loop?.Cancel();
        Jobs.Clear();
        Changed?.Invoke();
        if (!_remote.IsConfigured) return;
        _loop = new CancellationTokenSource();
        if (_clock == null)
        {
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (_, _) =>
            {
                foreach (var job in Jobs) if (job.IsRunning) job.Tick();
            };
        }
        _clock.Start();
        _ = FollowAsync(_loop.Token);
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop = null;
        _clock?.Stop();
        IsOnline = false;
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        long since = 0;
        var wait = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var page = await _remote.JobsAsync(since, ct);
                since = page.Revision;
                wait = TimeSpan.FromSeconds(1);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    IsOnline = true;
                    Merge(page.Jobs);
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Unreachable or restarted: start over from a full list once it answers again.
                await Dispatcher.UIThread.InvokeAsync(() => IsOnline = false);
                since = 0;
                var wake = _wake.Task;
                try { await Task.WhenAny(Task.Delay(wait, ct), wake); } catch (OperationCanceledException) { return; }
                if (ct.IsCancellationRequested) return;
                wait = wake.IsCompleted ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Min(10, wait.TotalSeconds * 2));
            }
        }
    }

    /// <summary>
    /// A phone just back from sleep, or a page change: if the computer was unreachable, try it again
    /// now instead of at the end of the back-off.
    /// </summary>
    public void Nudge()
    {
        if (_loop == null)
        {
            if (_remote.IsConfigured) Start();
            return;
        }
        var wake = _wake;
        _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wake.TrySetResult();
    }

    /// <summary>Applies one job the computer just returned, without waiting for the next poll.</summary>
    public JobVm Apply(JobDto dto)
    {
        var vm = Jobs.FirstOrDefault(j => j.Id == dto.Id);
        if (vm == null) Jobs.Insert(0, vm = new JobVm(dto));
        else vm.Update(dto);
        Changed?.Invoke();
        return vm;
    }

    public async Task<JobVm> CreateAsync(JobRequest request)
    {
        var dto = await _remote.CreateJobAsync(request);
        return Apply(dto);
    }

    public async Task CancelAsync(JobVm job)
    {
        try { Apply(await _remote.CancelAsync(job.Id)); }
        catch (RemoteException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { /* already over */ }
    }

    public async Task RetryAsync(JobVm job) => Apply(await _remote.RetryAsync(job.Id));

    public async Task RemoveAsync(JobVm job)
    {
        await _remote.DeleteJobAsync(job.Id);
        Jobs.Remove(job);
        Changed?.Invoke();
    }

    private void Merge(IReadOnlyList<JobDto> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var dto = list[i];
            var index = IndexOf(dto.Id);
            if (index < 0)
            {
                Jobs.Insert(Math.Min(i, Jobs.Count), new JobVm(dto));
                continue;
            }
            Jobs[index].Update(dto);
            if (index != i && i < Jobs.Count) Jobs.Move(index, i);
        }
        for (var i = Jobs.Count - 1; i >= 0; i--)
            if (list.All(d => d.Id != Jobs[i].Id)) Jobs.RemoveAt(i);
        Changed?.Invoke();
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < Jobs.Count; i++) if (Jobs[i].Id == id) return i;
        return -1;
    }
}
