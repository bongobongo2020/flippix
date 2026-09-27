using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.ViewModels;

/// <summary>
/// A story becomes a film on the computer: its writing assistant divides it into 10 second shots and
/// each shot is filmed with the same cast photos, so the people stay the same people.
/// </summary>
public partial class StoryViewModel : ObservableObject
{
    public const int MaxCast = 3;
    private readonly Action<IReadOnlyList<ViewerEntry>, int> _openViewer;

    public StoryViewModel(Action<IReadOnlyList<ViewerEntry>, int> openViewer)
    {
        _openViewer = openViewer;
        Pictures.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAddPicture));
            OnPropertyChanged(nameof(CastHint));
        };
        AppServices.Jobs.Changed += Sync;
    }

    public ObservableCollection<PictureSlot> Pictures { get; } = new();
    public ObservableCollection<JobVm> Films { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private string _story = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShort), nameof(IsMinute), nameof(IsLong))]
    private int _clipCount = 6;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private bool _isSending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddPicture))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private bool _isPreparing;

    [ObservableProperty] private string? _notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoFilms))]
    private bool _hasFilms;

    public bool NoFilms => !HasFilms;
    public bool IsShort => ClipCount == 3;
    public bool IsMinute => ClipCount == 6;
    public bool IsLong => ClipCount == 12;
    public bool CanAddPicture => !IsPreparing && Pictures.Count < MaxCast;
    public string CastHint => Pictures.Count == 0
        ? "Optional. Photos of the people in your story. Leave it empty and a portrait is made from the story."
        : $"{Pictures.Count} of {MaxCast}. The first person in the story is the first photo.";

    public async Task AddPhotosAsync(IEnumerable<Func<Task<Stream>>> openers)
    {
        Notice = null;
        IsPreparing = true;
        try
        {
            foreach (var open in openers)
            {
                if (Pictures.Count >= MaxCast) break;
                try
                {
                    await using var stream = await open();
                    Pictures.Add(PictureSlot.FromPhone(await Task.Run(() => ReferencePicture.FromStream(stream))));
                }
                catch (Exception ex)
                {
                    Notice = "That photo couldn't be opened: " + ImageViewModel.FirstLine(ex.Message);
                }
            }
        }
        finally
        {
            IsPreparing = false;
        }
    }

    [RelayCommand] private void RemovePicture(PictureSlot p) => Pictures.Remove(p);
    [RelayCommand] private void PickLength(string clips) => ClipCount = int.Parse(clips);

    private bool CanMake() => !IsSending && !IsPreparing && !string.IsNullOrWhiteSpace(Story);

    [RelayCommand(CanExecute = nameof(CanMake))]
    private async Task MakeAsync()
    {
        Notice = null;
        IsSending = true;
        try
        {
            var refs = new List<string>();
            foreach (var p in Pictures) refs.Add(await p.ReferenceAsync());
            await AppServices.Jobs.CreateAsync(new JobRequest
            {
                Kind = JobKinds.Story,
                Story = Story.Trim(),
                Clips = ClipCount,
                Pictures = refs,
            });
        }
        catch (RemoteException ex)
        {
            Notice = ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Every finished shot of a film, back to back.</summary>
    [RelayCommand]
    private void PlayFilm(JobVm film)
    {
        var urls = film.Items.Where(i => i.IsDone && i.FileUrl != null).Select(i => i.FileUrl!).ToList();
        if (urls.Count == 0) return;
        _openViewer(new[]
        {
            new ViewerEntry
            {
                Kind = MediaKinds.Video,
                Title = film.Title,
                Subtitle = urls.Count == film.Items.Count ? film.LengthLabel : $"{urls.Count} of {film.Items.Count} shots so far",
                Playlist = urls,
            },
        }, 0);
    }

    [RelayCommand]
    private void SelectClip(JobItemVm clip)
    {
        var film = clip.Job;
        if (film.Selected != null) film.Selected.IsSelected = false;
        film.Selected = film.Selected == clip ? null : clip;
        if (film.Selected != null) film.Selected.IsSelected = true;
    }

    [RelayCommand]
    private void PlayClip(JobItemVm clip)
    {
        if (!clip.IsDone) return;
        var done = clip.Job.Items.Where(i => i.IsDone).ToList();
        _openViewer(done.Select(ViewerEntry.FromJobItem).ToList(), done.IndexOf(clip));
    }

    [RelayCommand]
    private async Task RetryAsync(JobVm film)
    {
        try { await AppServices.Jobs.RetryAsync(film); }
        catch (RemoteException ex) { Notice = ex.Message; }
    }

    [RelayCommand]
    private async Task StopAsync(JobVm film)
    {
        try { await AppServices.Jobs.CancelAsync(film); }
        catch (RemoteException ex) { Notice = ex.Message; }
    }

    [RelayCommand]
    private async Task RemoveAsync(JobVm film)
    {
        try { await AppServices.Jobs.RemoveAsync(film); }
        catch (RemoteException ex) { Notice = ex.Message; }
    }

    private void Sync()
    {
        CollectionSync.Apply(Films, AppServices.Jobs.Jobs.Where(j => j.Kind == JobKinds.Story).ToList());
        HasFilms = Films.Count > 0;
    }
}
