using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FlipPix.UI.Controls;

/// <summary>
/// A search box control for filtering tabs in the application.
/// Provides quick navigation to tabs using keyboard shortcuts (Ctrl+K).
/// </summary>
public partial class TabSearchBox : System.Windows.Controls.UserControl
{
    /// <summary>
    /// Event raised when the search text changes.
    /// </summary>
    public event EventHandler<string>? SearchTextChanged;

    /// <summary>
    /// Event raised when the user presses Enter to confirm selection.
    /// </summary>
    public event EventHandler? SearchConfirmed;

    /// <summary>
    /// Event raised when the user presses Escape to cancel search.
    /// </summary>
    public event EventHandler? SearchCancelled;

    /// <summary>
    /// Event raised when the user presses Up/Down to navigate results.
    /// </summary>
    public event EventHandler<int>? NavigateResults;

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            nameof(PlaceholderText),
            typeof(string),
            typeof(TabSearchBox),
            new PropertyMetadata("Search tabs... (Ctrl+K)"));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public string SearchText
    {
        get => SearchTextBox.Text;
        set => SearchTextBox.Text = value;
    }

    public TabSearchBox()
    {
        InitializeComponent();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearButton.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        SearchTextChanged?.Invoke(this, SearchTextBox.Text);
    }

    private void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                SearchConfirmed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;

            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchTextBox.Text))
                {
                    SearchTextBox.Text = string.Empty;
                }
                else
                {
                    SearchCancelled?.Invoke(this, EventArgs.Empty);
                }
                e.Handled = true;
                break;

            case Key.Up:
                NavigateResults?.Invoke(this, -1);
                e.Handled = true;
                break;

            case Key.Down:
                NavigateResults?.Invoke(this, 1);
                e.Handled = true;
                break;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        SearchTextBox.Focus();
    }

    /// <summary>
    /// Focuses the search box and selects all text.
    /// </summary>
    public void FocusAndSelectAll()
    {
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    /// <summary>
    /// Clears the search text.
    /// </summary>
    public void Clear()
    {
        SearchTextBox.Text = string.Empty;
    }
}
