using Avalonia.Controls;
using Avalonia.Interactivity;
using FlipPix.UI.ViewModels;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// 📚 Story Prompts — see <see cref="StoryPromptLibraryViewModel"/>. The window only supplies the two
/// dialogs the view model asks through and stops a close that would lose unsaved edits.
/// </summary>
public partial class StoryPromptLibraryWindow : Window
{
    private bool _closeConfirmed;

    public StoryPromptLibraryWindow() : this(null!) { }

    public StoryPromptLibraryWindow(StoryPromptLibraryViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        if (viewModel != null)
        {
            viewModel.ConfirmUnsaved = AskUnsaved;
            viewModel.Confirm = AskYesNo;
        }
    }

    public StoryPromptLibraryViewModel ViewModel { get; }

    private static UnsavedChoice AskUnsaved(string message)
    {
        var answer = MessageBox.Show(message + "\n\nSave them before moving on?", "Story Prompts",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => UnsavedChoice.Save,
            MessageBoxResult.No => UnsavedChoice.Discard,
            _ => UnsavedChoice.Stay
        };
    }

    private static bool AskYesNo(string message) =>
        MessageBox.Show(message, "Story Prompts", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The unsaved-edits question is a dialog, which cannot run inside Closing here: cancel,
        // ask once the close has unwound, and close again if the answer allows it.
        if (!_closeConfirmed && ViewModel != null)
        {
            e.Cancel = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ViewModel.ResolveUnsaved()) return;
                _closeConfirmed = true;
                Close();
            });
        }
        base.OnClosing(e);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
