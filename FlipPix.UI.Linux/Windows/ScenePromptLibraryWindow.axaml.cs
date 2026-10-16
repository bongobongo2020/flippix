using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// Picker over one tab's saved prompt library: search, preview, rename, delete, and hand one back to
/// the tab. Port of FlipPix.UI/ScenePromptLibraryWindow.xaml.cs; shown through the
/// FlipPix.UI.ScenePromptLibraryWindow facade, which receives the chosen scene and the dialog result.
/// </summary>
public partial class ScenePromptLibraryWindow : Window
{
    private readonly ScenePromptLibrary _library = null!;
    private readonly List<ScenePrompt> _entries = new();
    private readonly ScenePromptLibraryChrome _chrome = new();
    private readonly FlipPix.UI.ScenePromptLibraryWindow? _facade;
    private readonly ObservableCollection<Row> _rows = new();

    public ScenePromptLibraryWindow()
    {
        InitializeComponent();
        ScenesList.ItemsSource = _rows;
    }

    public ScenePromptLibraryWindow(ScenePromptLibrary library, List<ScenePrompt> entries,
        ScenePromptLibraryChrome? chrome, FlipPix.UI.ScenePromptLibraryWindow? facade)
    {
        InitializeComponent();
        _library = library;
        _entries = entries;
        _chrome = chrome ?? new ScenePromptLibraryChrome();
        _facade = facade;

        ApplyChrome();
        ScenesList.ItemsSource = _rows;
        Rebuild(string.Empty);
    }

    /// <summary>Retitles the window for whichever tab opened it, and repaints its accent.</summary>
    private void ApplyChrome()
    {
        Title = _chrome.WindowTitle;
        HeadingText.Text = _chrome.Heading;
        SubtitleText.Text = _chrome.Subtitle;
        PromptNote.Text = _chrome.PromptNote;
        UseButton.Content = _chrome.UseButtonText;
        PromptBox.IsReadOnly = !_chrome.PromptEditable;

        if (_chrome.AccentColorKey != null &&
            Avalonia.Application.Current?.TryFindResource(_chrome.AccentColorKey, out var res) == true && res is Color accent)
        {
            Resources["TabAccentBrush"] = new SolidColorBrush(accent);
        }
    }

    private static string ComposePreview(ScenePrompt entry)
    {
        if (entry.ContinuationPrompts.Count == 0) return entry.Prompt;
        var text = new System.Text.StringBuilder();
        text.Append("=== SEGMENT 1 ===\n").Append(entry.Prompt);
        for (var i = 0; i < entry.ContinuationPrompts.Count; i++)
            text.Append("\n\n=== SEGMENT ").Append(i + 2).Append(" ===\n").Append(entry.ContinuationPrompts[i]);
        return text.ToString();
    }

    private void Rebuild(string? filter)
    {
        var previous = (ScenesList.SelectedItem as Row)?.Entry;
        _rows.Clear();

        IEnumerable<ScenePrompt> source = _entries.OrderByDescending(e => e.LastUsed);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var needle = filter.Trim();
            source = source.Where(e =>
                e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                e.Prompt.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                e.ContinuationPrompts.Any(p => p.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var entry in source)
            _rows.Add(new Row(entry, _library?.ResolveThumbnail(entry) != null));

        EmptyText.IsVisible = _rows.Count == 0;
        EmptyText.Text = _entries.Count == 0 ? _chrome.EmptyText : _chrome.NoMatchText;

        if (previous != null)
            ScenesList.SelectedItem = _rows.FirstOrDefault(r => ReferenceEquals(r.Entry, previous));

        _ = LoadThumbnailsAsync();
    }

    private async Task LoadThumbnailsAsync()
    {
        if (_library == null) return;
        var pending = _rows.Where(r => r.HasThumbnail && r.Thumbnail == null).ToList();
        foreach (var row in pending)
        {
            var bitmap = await Task.Run(() => _library.LoadThumbnail(row.Entry));
            if (bitmap != null) row.Thumbnail = bitmap;
        }
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => Rebuild(SearchBox.Text);

    private void ScenesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var row = ScenesList.SelectedItem as Row;
        NameBox.Text = row?.Entry.Name ?? string.Empty;
        PromptBox.Text = row == null ? string.Empty : ComposePreview(row.Entry);
        NameBox.IsEnabled = row != null;
        PromptBox.IsEnabled = row != null;
        UseButton.IsEnabled = row != null;
        DeleteButton.IsEnabled = row != null;
    }

    private void ScenesList_MouseDoubleClick(object? sender, TappedEventArgs e)
    {
        if (ScenesList.SelectedItem is Row) Use();
    }

    private void NameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (ScenesList.SelectedItem is not Row row) return;
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0 || name == row.Entry.Name)
        {
            NameBox.Text = row.Entry.Name;
            return;
        }
        row.Entry.Name = name;
        row.Refresh();
        _ = _library.SaveAsync(_entries);
    }

    private void PromptBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_chrome.PromptEditable) return;
        if (ScenesList.SelectedItem is not Row row) return;
        if (row.Entry.ContinuationPrompts.Count > 0) return;

        var text = (PromptBox.Text ?? string.Empty).Trim();
        if (text.Length == 0 || text == row.Entry.Prompt)
        {
            PromptBox.Text = row.Entry.Prompt;
            return;
        }
        row.Entry.Prompt = text;
        row.Entry.Shots = CountShots(text);
        row.Entry.LastUsed = DateTime.Now;
        row.Refresh();
        _ = _library.SaveAsync(_entries);
    }

    private static int CountShots(string prompt) =>
        Regex.Matches(prompt, @"\[\s*Shot\s+\d+", RegexOptions.IgnoreCase).Count;

    private static int CountClips(string prompt) =>
        Regex.Matches(prompt, @"^[ \t]*[=#*\-–—\[]{0,6}[ \t]*CLIP[ \t]+\d+\b",
                      RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;

    private void DeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ScenesList.SelectedItem is not Row row) return;
        var confirm = MessageBox.Show(
            $"Delete \"{row.Entry.Name}\" from the {_chrome.LibraryNoun}?\n\nThis cannot be undone.",
            "Delete Scene", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _library.DeleteThumbnail(row.Entry.ThumbnailFile);
        _entries.Remove(row.Entry);
        _rows.Remove(row);
        _library.Save(_entries);
        Rebuild(SearchBox.Text);
    }

    private void UseButton_Click(object? sender, RoutedEventArgs e) => Use();

    private void Use()
    {
        if (ScenesList.SelectedItem is not Row row) return;
        row.Entry.LastUsed = DateTime.Now;
        row.Entry.UseCount++;
        _library.Save(_entries);

        if (_facade != null)
        {
            _facade.SelectedScene = row.Entry;
            _facade.DialogResult = true;
        }
        Close();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_facade != null) _facade.DialogResult = false;
        Close();
    }

    /// <summary>One list row. The thumbnail arrives after construction, hence the change notification.</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private System.Windows.Media.Imaging.BitmapImage? _thumbnail;

        public Row(ScenePrompt entry, bool hasThumbnail)
        {
            Entry = entry;
            HasThumbnail = hasThumbnail;
        }

        public ScenePrompt Entry { get; }
        public bool HasThumbnail { get; }
        public string Name => Entry.Name;

        public string Meta
        {
            get
            {
                var parts = new List<string> { Entry.LastUsed.ToString("d MMM yyyy") };
                if (Entry.ContinuationPrompts.Count > 0) parts.Add($"{Entry.ContinuationPrompts.Count + 1} passes");
                var clips = CountClips(Entry.Prompt);
                if (clips > 1) parts.Add($"{clips} clips");
                if (Entry.Shots > 0) parts.Add($"{Entry.Shots} shots");
                if (Entry.LengthSeconds > 0) parts.Add($"{Entry.LengthSeconds:0.#}s");
                if (!string.IsNullOrEmpty(Entry.AspectRatio)) parts.Add(Entry.AspectRatio);
                if (Entry.UseCount > 0) parts.Add($"used {Entry.UseCount}×");
                return string.Join(" · ", parts);
            }
        }

        public System.Windows.Media.Imaging.BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); }
        }

        public void Refresh()
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Meta));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
