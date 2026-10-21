using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FlipPix.UI.Linux.Controls;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// Plays one video. On a Chromebook it stands in for the OS video player, which Linux apps
/// can't launch (see FlipPix.UI.Services.VideoOpener).
/// </summary>
public partial class VideoPlayerWindow : Window
{
    private readonly string _path;
    private readonly DispatcherTimer _clock;
    private bool _playing;
    private bool _movingSlider; // the clock is moving the slider, so don't seek

    // Parameterless constructor for the XAML loader and designer.
    public VideoPlayerWindow() : this("") { }

    public VideoPlayerWindow(string path)
    {
        _path = path;
        InitializeComponent();
        Title = string.IsNullOrEmpty(path) ? "Video" : Path.GetFileName(path);
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _clock.Tick += (_, _) => UpdateClock();
        KeyDown += OnKeyDown;
        Opened += (_, _) =>
        {
            if (string.IsNullOrEmpty(_path)) return;
            Player.Source = _path;
            Play();
            _clock.Start();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        Player.Close();
        base.OnClosed(e);
    }

    private void Play()
    {
        Player.Play();
        _playing = true;
        PlayPauseButton.Content = "⏸";
    }

    private void Pause()
    {
        Player.Pause();
        _playing = false;
        PlayPauseButton.Content = "▶";
    }

    private void TogglePlay()
    {
        if (_playing) Pause(); else Play();
    }

    private void UpdateClock()
    {
        var length = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        var position = Player.Position;
        if (length > TimeSpan.Zero)
        {
            _movingSlider = true;
            SeekSlider.Maximum = length.TotalSeconds;
            SeekSlider.Value = Math.Min(position.TotalSeconds, length.TotalSeconds);
            _movingSlider = false;
        }
        TimeText.Text = $"{Format(position)} / {Format(length)}";
    }

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void Player_MediaOpened(object? sender, RoutedEventArgs e)
    {
        StatusText.IsVisible = false;
        UpdateClock();
    }

    private void Player_MediaEnded(object? sender, RoutedEventArgs e)
    {
        if (LoopToggle.IsChecked == true)
        {
            Player.Position = TimeSpan.Zero;
            Play();
        }
        else
        {
            _playing = false;
            PlayPauseButton.Content = "▶";
        }
    }

    private void Player_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        StatusText.Text = $"Couldn't play this video: {e.ErrorException.Message}";
        StatusText.IsVisible = true;
        _playing = false;
        PlayPauseButton.Content = "▶";
    }

    private void Seek_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_movingSlider) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        UpdateClock();
    }

    private void Player_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Player).Properties.IsLeftButtonPressed) TogglePlay();
    }

    private void PlayPause_Click(object? sender, RoutedEventArgs e) => TogglePlay();

    private void Mute_Changed(object? sender, RoutedEventArgs e)
        => Player.IsMuted = MuteToggle.IsChecked == true;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Space: TogglePlay(); break;
            case Key.M: MuteToggle.IsChecked = MuteToggle.IsChecked != true; break;
            case Key.Left: Player.Position = Player.Position - TimeSpan.FromSeconds(5); UpdateClock(); break;
            case Key.Right: Player.Position = Player.Position + TimeSpan.FromSeconds(5); UpdateClock(); break;
            default: return;
        }
        e.Handled = true;
    }
}
