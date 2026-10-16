// The WPF window names the shared view models construct directly. Each is a System.Windows.Window
// facade (Compat/WpfWindow.cs) that shows the Avalonia port of that window from Windows/.

using System.Collections.Generic;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;
using FlipPix.UI.ViewModels.Video;
using AvWindow = Avalonia.Controls.Window;

namespace FlipPix.UI
{
    /// <summary>
    /// The wording and accent one tab wants on the shared library picker; see the WPF
    /// ScenePromptLibraryWindow.xaml.cs, where this type is defined for the Windows build.
    /// </summary>
    public sealed class ScenePromptLibraryChrome
    {
        public string WindowTitle { get; init; } = "Scene Library";
        public string Heading { get; init; } = "Saved scenes";

        public string Subtitle { get; init; } =
            "Every prompt this tab analyzes is saved here. Pick one to drop it into the H3 prompt box — "
            + "the reference line is rewritten for whichever character images are loaded.";

        public string PromptNote { get; init; } =
            "Stored without its reference line — that line is written fresh from the character images "
            + "loaded in the tab.";

        public string EmptyText { get; init; } =
            "No saved scenes yet.\nAnalyze a scene image and it lands here.";

        public string NoMatchText { get; init; } = "No scene matches that search.";
        public string UseButtonText { get; init; } = "✓ Use This Scene";
        public string LibraryNoun { get; init; } = "scene library";
        public string? AccentColorKey { get; init; }
        public bool PromptEditable { get; init; }
    }

    public sealed class ScenePromptLibraryWindow : System.Windows.Window
    {
        private readonly ScenePromptLibrary _library;
        private readonly List<ScenePrompt> _entries;
        private readonly ScenePromptLibraryChrome _chrome;

        public ScenePromptLibraryWindow(
            ScenePromptLibrary library, List<ScenePrompt> entries, ScenePromptLibraryChrome? chrome = null)
        {
            _library = library;
            _entries = entries;
            _chrome = chrome ?? new ScenePromptLibraryChrome();
        }

        /// <summary>The scene the user chose, set only when the dialog returns true.</summary>
        public ScenePrompt? SelectedScene { get; internal set; }

        protected override AvWindow CreateView()
            => new Linux.Windows.ScenePromptLibraryWindow(_library, _entries, _chrome, this);
    }

    public sealed class ExpressJobWindow : System.Windows.Window
    {
        public ExpressJobWindow(ExpressJobViewModel viewModel) => ViewModel = viewModel;

        public ExpressJobViewModel ViewModel { get; }

        protected override AvWindow CreateView() => new Linux.Windows.ExpressJobWindow(ViewModel, this);
    }

    public sealed class StoryPromptLibraryWindow : System.Windows.Window
    {
        public StoryPromptLibraryWindow(StoryPromptLibraryViewModel viewModel) => ViewModel = viewModel;

        public StoryPromptLibraryViewModel ViewModel { get; }

        protected override AvWindow CreateView() => new Linux.Windows.StoryPromptLibraryWindow(ViewModel);
    }
}
