using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace FlipPix.UI
{
    /// <summary>
    /// Picker over the MiniMax Character tab's saved scene prompts: search, preview, rename, delete, and
    /// hand one back to the tab. <c>DialogResult == true</c> means <see cref="SelectedScene"/> should be
    /// loaded into the prompt box.
    ///
    /// <para>The window owns the persisted copy while it is open — renames and deletes are written straight
    /// through, so closing with Close (rather than Use) still keeps them.</para>
    /// </summary>
    public partial class ScenePromptLibraryWindow : Window
    {
        private readonly ScenePromptLibrary _library;
        private readonly List<ScenePrompt> _entries;
        private readonly ObservableCollection<Row> _rows = new();

        /// <summary>The scene the user chose, set only when the dialog returns true.</summary>
        public ScenePrompt? SelectedScene { get; private set; }

        public ScenePromptLibraryWindow(ScenePromptLibrary library, List<ScenePrompt> entries)
        {
            InitializeComponent();
            _library = library;
            _entries = entries;

            ScenesList.ItemsSource = _rows;
            Rebuild(string.Empty);
            _ = LoadThumbnailsAsync();
        }

        /// <summary>Refills the list from <see cref="_entries"/>, keeping only rows matching the filter.</summary>
        private void Rebuild(string filter)
        {
            var previous = (ScenesList.SelectedItem as Row)?.Entry;
            _rows.Clear();

            IEnumerable<ScenePrompt> source = _entries.OrderByDescending(e => e.LastUsed);
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var needle = filter.Trim();
                source = source.Where(e =>
                    e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    e.Prompt.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var entry in source)
                _rows.Add(new Row(entry, _library.ResolveThumbnail(entry) != null));

            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = _entries.Count == 0
                ? "No saved scenes yet.\nAnalyze a scene image and it lands here."
                : "No scene matches that search.";

            if (previous != null)
                ScenesList.SelectedItem = _rows.FirstOrDefault(r => ReferenceEquals(r.Entry, previous));

            _ = LoadThumbnailsAsync();
        }

        /// <summary>
        /// Decodes thumbnails off the UI thread and pushes them into the rows as they arrive, so opening the
        /// window with a few hundred saved scenes is not a stall.
        /// </summary>
        private async Task LoadThumbnailsAsync()
        {
            var pending = _rows.Where(r => r.HasThumbnail && r.Thumbnail == null).ToList();
            if (pending.Count == 0) return;

            foreach (var row in pending)
            {
                var bitmap = await Task.Run(() => _library.LoadThumbnail(row.Entry));
                if (bitmap != null) row.Thumbnail = bitmap;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Rebuild(SearchBox.Text);

        private void ScenesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = ScenesList.SelectedItem as Row;

            NameBox.Text = row?.Entry.Name ?? string.Empty;
            PromptBox.Text = row?.Entry.Prompt ?? string.Empty;

            NameBox.IsEnabled = row != null;
            PromptBox.IsEnabled = row != null;
            UseButton.IsEnabled = row != null;
            DeleteButton.IsEnabled = row != null;
        }

        private void ScenesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ScenesList.SelectedItem is Row) Use();
        }

        private void NameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ScenesList.SelectedItem is not Row row) return;

            var name = NameBox.Text.Trim();
            if (name.Length == 0 || name == row.Entry.Name)
            {
                NameBox.Text = row.Entry.Name;
                return;
            }

            row.Entry.Name = name;
            row.Refresh();
            _ = _library.SaveAsync(_entries);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (ScenesList.SelectedItem is not Row row) return;

            var confirm = MessageBox.Show(
                $"Delete \"{row.Entry.Name}\" from the scene library?\n\nThis cannot be undone.",
                "Delete Scene", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _library.DeleteThumbnail(row.Entry.ThumbnailFile);
            _entries.Remove(row.Entry);
            _rows.Remove(row);

            _library.Save(_entries);
            Rebuild(SearchBox.Text);
        }

        private void UseButton_Click(object sender, RoutedEventArgs e) => Use();

        private void Use()
        {
            if (ScenesList.SelectedItem is not Row row) return;

            row.Entry.LastUsed = DateTime.Now;
            row.Entry.UseCount++;
            _library.Save(_entries);

            SelectedScene = row.Entry;
            DialogResult = true;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>One list row. The thumbnail arrives after construction, hence the change notification.</summary>
        private sealed class Row : INotifyPropertyChanged
        {
            private BitmapImage? _thumbnail;

            public Row(ScenePrompt entry, bool hasThumbnail)
            {
                Entry = entry;
                HasThumbnail = hasThumbnail;
            }

            public ScenePrompt Entry { get; }
            public bool HasThumbnail { get; }

            public string Name => Entry.Name;

            /// <summary>Second line: enough to tell two similar scenes apart at a glance.</summary>
            public string Meta
            {
                get
                {
                    var parts = new List<string> { Entry.LastUsed.ToString("d MMM yyyy") };
                    if (Entry.Shots > 0) parts.Add($"{Entry.Shots} shots");
                    if (Entry.LengthSeconds > 0) parts.Add($"{Entry.LengthSeconds:0.#}s");
                    if (!string.IsNullOrEmpty(Entry.AspectRatio)) parts.Add(Entry.AspectRatio);
                    if (Entry.UseCount > 0) parts.Add($"used {Entry.UseCount}×");
                    return string.Join(" · ", parts);
                }
            }

            public BitmapImage? Thumbnail
            {
                get => _thumbnail;
                set { _thumbnail = value; OnPropertyChanged(); }
            }

            /// <summary>Re-reads the entry after a rename.</summary>
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
}
