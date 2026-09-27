using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.ViewModels;

public enum ConnectStep { Find, Code, Connected }

/// <summary>
/// Pairing with a FlipPix desktop: find it on the Wi-Fi (or type its address), type the code it
/// shows, done. Once paired, this page shows what the computer can do right now.
/// </summary>
public partial class ConnectViewModel : ObservableObject
{
    private readonly Action _onPaired;
    private readonly Action _onForgotten;
    private FoundComputer? _target;
    private CancellationTokenSource? _search;

    public ConnectViewModel(Action onPaired, Action onForgotten)
    {
        _onPaired = onPaired;
        _onForgotten = onForgotten;
        _step = AppServices.Settings.IsPaired ? ConnectStep.Connected : ConnectStep.Find;
        _computerName = AppServices.Settings.ServerName;
    }

    public ObservableCollection<FoundComputer> Found { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFind), nameof(IsCode), nameof(IsConnected))]
    private ConnectStep _step;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingFound))]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseAddressCommand))]
    private string _address = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    private string _code = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand), nameof(UseAddressCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _computerName = "";
    [ObservableProperty] private string _computerAddress = "";

    // What the paired computer reports.
    [ObservableProperty] private bool _statusKnown;
    [ObservableProperty] private bool _comfyOnline;
    [ObservableProperty] private string _comfyText = "";
    [ObservableProperty] private bool _hasLlm;
    [ObservableProperty] private string _llmText = "";
    [ObservableProperty] private bool _libraryReachable;
    [ObservableProperty] private string _libraryText = "";
    [ObservableProperty] private string _queueText = "";

    public bool IsFind => Step == ConnectStep.Find;
    public bool IsCode => Step == ConnectStep.Code;
    public bool IsConnected => Step == ConnectStep.Connected;
    public bool NothingFound => !IsSearching && Found.Count == 0;

    /// <summary>Called whenever the page opens.</summary>
    public void OnShown()
    {
        Notice = null;
        if (Step == ConnectStep.Find) _ = SearchAsync();
        else if (Step == ConnectStep.Connected) _ = RefreshStatusAsync();
    }

    /// <summary>The computer removed this phone: back to the start, saying why.</summary>
    public void ShowUnpaired()
    {
        Step = ConnectStep.Find;
        Notice = "The computer doesn't know this phone any more. Pair it again.";
        _ = SearchAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        IsSearching = true;
        try
        {
            var found = await Discovery.FindAsync(TimeSpan.FromSeconds(2), search.Token);
            if (search.IsCancellationRequested) return;
            Found.Clear();
            foreach (var f in found) Found.Add(f);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception)
        {
            // No broadcast on this network (some guest Wi-Fi blocks it): typing the address still works.
            Found.Clear();
        }
        finally
        {
            if (_search == search)
            {
                IsSearching = false;
                OnPropertyChanged(nameof(NothingFound));
            }
        }
    }

    [RelayCommand]
    private void Pick(FoundComputer computer)
    {
        _target = computer;
        ComputerName = computer.Name;
        ComputerAddress = computer.Address;
        Code = "";
        Notice = null;
        Step = ConnectStep.Code;
    }

    private bool CanUseAddress() => !IsBusy && Address.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanUseAddress))]
    private async Task UseAddressAsync()
    {
        Notice = null;
        IsBusy = true;
        try
        {
            var url = MobileSettings.NormalizeUrl(Address);
            var hello = await RemoteClient.HelloAsync(url);
            Pick(new FoundComputer(hello.Name, url));
        }
        catch (RemoteException ex)
        {
            Notice = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        Notice = null;
        Step = ConnectStep.Find;
        _ = SearchAsync();
    }

    private bool CanPair() => !IsBusy && Code.Count(char.IsAsciiDigit) == 6;

    [RelayCommand(CanExecute = nameof(CanPair))]
    private async Task PairAsync()
    {
        if (_target == null) return;
        Notice = null;
        IsBusy = true;
        try
        {
            var paired = await RemoteClient.PairAsync(_target.Url, Code, DeviceInfo.Name);
            var settings = AppServices.Settings;
            settings.ServerUrl = _target.Url;
            settings.ServerName = paired.Name;
            settings.Token = paired.Token;
            settings.Save();
            AppServices.Remote.Configure(settings.ServerUrl, settings.Token, settings.ServerName);
            ComputerName = paired.Name;
            ComputerAddress = _target.Address;
            Code = "";
            Step = ConnectStep.Connected;
            _onPaired();
            _ = RefreshStatusAsync();
        }
        catch (RemoteException ex)
        {
            Notice = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        ComputerName = AppServices.Settings.ServerName;
        ComputerAddress = AppServices.Settings.ServerUrl.Replace("http://", "", StringComparison.Ordinal);
        try
        {
            var s = await AppServices.Remote.StatusAsync();
            ComputerName = s.Name;
            ComfyOnline = s.ComfyOnline;
            ComfyText = s.ComfyOnline ? "Ready to make things" : $"Not answering at {s.ComfyUrl}";
            HasLlm = s.HasLlm;
            LlmText = s.HasLlm ? s.LlmLabel : "None set. Videos use your words as written; stories need one.";
            LibraryReachable = s.LibraryReachable;
            LibraryText = s.LibraryReachable ? s.LibraryFolder : $"Can't reach {s.LibraryFolder}";
            QueueText = (s.Running, s.Queued) switch
            {
                (0, 0) => "Nothing in the queue.",
                (_, 0) => "Making something now.",
                _ => $"Making something now, {s.Queued} more waiting.",
            };
            StatusKnown = true;
            Notice = null;
        }
        catch (RemoteException ex)
        {
            StatusKnown = false;
            Notice = ex.Message;
        }
    }

    /// <summary>Unpairs this phone. The computer keeps listing it until removed there too.</summary>
    [RelayCommand]
    private void Forget()
    {
        var settings = AppServices.Settings;
        settings.Token = "";
        settings.Save();
        AppServices.Remote.Configure("", "", "");
        StatusKnown = false;
        Step = ConnectStep.Find;
        _onForgotten();
        _ = SearchAsync();
    }
}
