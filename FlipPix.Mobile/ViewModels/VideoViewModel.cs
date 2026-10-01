using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.ViewModels;

/// <summary>
/// Photos plus a sentence become a video (MiniMax I2V on the computer). The photos are references:
/// who the people are and where they stand, not the first frame. The computer's writing assistant
/// turns the sentence into a full scene with sound, looking at the photos.
/// </summary>
public partial class VideoViewModel : ObservableObject
{
    public const int MaxPictures = 4;

    /// <summary>"Video by MiniMax H3": its license requires the model's name on screen where video is made.</summary>
    public string VideoCredit => ServerInfo.Current.VideoCredit;
    public bool HasVideoCredit => ServerInfo.Current.HasVideoCredit;
    private readonly Action<IReadOnlyList<ViewerEntry>, int> _openViewer;

    public VideoViewModel(Action<IReadOnlyList<ViewerEntry>, int> openViewer)
    {
        _openViewer = openViewer;
        ServerInfo.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ServerInfo.VideoCredit) or nameof(ServerInfo.HasVideoCredit))
            {
                OnPropertyChanged(nameof(VideoCredit));
                OnPropertyChanged(nameof(HasVideoCredit));
            }
        };
        Pictures.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAddPicture));
            OnPropertyChanged(nameof(HasPictures));
            OnPropertyChanged(nameof(PictureHint));
            MakeCommand.NotifyCanExecuteChanged();
            SuggestCommand.NotifyCanExecuteChanged();
        };
        AppServices.Jobs.Changed += Sync;
    }

    public ObservableCollection<PictureSlot> Pictures { get; } = new();
    public ObservableCollection<JobVm> Takes { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private string _idea = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFive), nameof(IsTen), nameof(IsFifteen))]
    private int _seconds = 10;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand), nameof(SuggestCommand))]
    private bool _isSending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand), nameof(SuggestCommand))]
    private bool _isSuggesting;

    /// <summary>A picked photo is being rotated, shrunk and encoded; shown as a placeholder tile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddPicture))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private bool _isPreparing;

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _sendingText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoTakes))]
    private bool _hasTakes;

    public bool NoTakes => !HasTakes;
    public bool IsFive => Seconds == 5;
    public bool IsTen => Seconds == 10;
    public bool IsFifteen => Seconds == 15;
    public bool CanAddPicture => !IsPreparing && Pictures.Count < MaxPictures;
    public bool HasPictures => Pictures.Count > 0;
    public string PictureHint => Pictures.Count switch
    {
        0 => "Photos of the people and the place, up to four. Or open a picture in the Library and tap Animate.",
        MaxPictures => "Four is the most the model takes. Tap one to remove it.",
        _ => $"{Pictures.Count} of {MaxPictures}. The first photo sets the video's shape.",
    };

    /// <summary>Called by the view with the streams the system photo picker returned.</summary>
    public async Task AddPhotosAsync(IEnumerable<Func<Task<Stream>>> openers)
    {
        Notice = null;
        IsPreparing = true;
        try
        {
            foreach (var open in openers)
            {
                if (Pictures.Count >= MaxPictures) break;
                try
                {
                    await using var stream = await open();
                    // Rotating and shrinking a 50 MP photo is real CPU work; keep it off the UI thread.
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

    /// <summary>From the viewer's Animate: a picture already on the computer.</summary>
    public void AddFromLibrary(PictureSlot slot)
    {
        Notice = null;
        if (Pictures.Count >= MaxPictures) Pictures.RemoveAt(Pictures.Count - 1);
        Pictures.Insert(0, slot);
    }

    [RelayCommand] private void RemovePicture(PictureSlot picture) => Pictures.Remove(picture);
    [RelayCommand] private void PickSeconds(string s) => Seconds = int.Parse(s);

    private bool CanMake() => !IsSending && !IsSuggesting && !IsPreparing && Pictures.Count > 0;

    [RelayCommand(CanExecute = nameof(CanMake))]
    private async Task MakeAsync()
    {
        Notice = null;
        IsSending = true;
        SendingText = "Sending photos";
        try
        {
            var refs = new List<string>();
            foreach (var p in Pictures) refs.Add(await p.ReferenceAsync());
            SendingText = "Asking the computer";
            await AppServices.Jobs.CreateAsync(new JobRequest
            {
                Kind = JobKinds.Video,
                Idea = Idea.Trim(),
                Seconds = Seconds,
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

    private bool CanSuggest() => !IsSending && !IsSuggesting && Pictures.Count > 0;

    /// <summary>The writing assistant looks at the first photo and proposes what could happen in it.</summary>
    [RelayCommand(CanExecute = nameof(CanSuggest))]
    private async Task SuggestAsync()
    {
        Notice = null;
        IsSuggesting = true;
        try
        {
            var reference = await Pictures[0].ReferenceAsync();
            Idea = await AppServices.Remote.AssistAsync(new AssistRequest { Task = AssistTasks.VideoIdea, Picture = reference, Text = Idea.Trim() });
        }
        catch (RemoteException ex)
        {
            Notice = ex.Message;
        }
        finally
        {
            IsSuggesting = false;
        }
    }

    /// <summary>The same scene again on a new seed: another performance of it.</summary>
    [RelayCommand]
    private async Task RetakeAsync(JobVm take)
    {
        if (take.Script == null) return;
        try
        {
            await AppServices.Jobs.CreateAsync(new JobRequest
            {
                Kind = JobKinds.Video,
                Idea = take.Request.Idea,
                Seconds = take.Request.Seconds,
                Pictures = take.Request.Pictures,
                Script = take.Script,
            });
        }
        catch (RemoteException ex)
        {
            Notice = ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenAsync(JobVm take)
    {
        if (take.IsDone && take.Items.FirstOrDefault(i => i.IsDone) is { } item)
        {
            var done = Takes.Where(t => t.IsDone).Select(t => t.Items.FirstOrDefault(i => i.IsDone)).OfType<JobItemVm>().ToList();
            _openViewer(done.Select(ViewerEntry.FromJobItem).ToList(), done.IndexOf(item));
        }
        else if (take.ShowRetry)
        {
            await RetryAsync(take);
        }
        else
        {
            take.ShowScript = !take.ShowScript;
        }
    }

    [RelayCommand] private void ToggleScript(JobVm take) => take.ShowScript = !take.ShowScript;

    [RelayCommand]
    private async Task RetryAsync(JobVm take)
    {
        try { await AppServices.Jobs.RetryAsync(take); }
        catch (RemoteException ex) { Notice = ex.Message; }
    }

    [RelayCommand]
    private async Task StopAsync(JobVm take)
    {
        try { await AppServices.Jobs.CancelAsync(take); }
        catch (RemoteException ex) { Notice = ex.Message; }
    }

    [RelayCommand]
    private async Task RemoveAsync(JobVm take)
    {
        try { await AppServices.Jobs.RemoveAsync(take); }
        catch (RemoteException ex) { Notice = ex.Message; }
    }

    private void Sync()
    {
        CollectionSync.Apply(Takes, AppServices.Jobs.Jobs.Where(j => j.Kind == JobKinds.Video).ToList());
        HasTakes = Takes.Count > 0;
    }
}
