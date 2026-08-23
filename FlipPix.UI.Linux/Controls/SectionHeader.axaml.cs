using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Controls
{
    /// <summary>
    /// Compact section heading (accent dot + all-caps label) used inside the side rail of a
    /// two-column tab. The dot picks up the enclosing tab's TabAccentBrush, so tabs stay
    /// visually distinct without repeating a colour on every header.
    /// </summary>
    public partial class SectionHeader : UserControl
    {
        public static readonly StyledProperty<string> TitleProperty =
            AvaloniaProperty.Register<SectionHeader, string>(nameof(Title), string.Empty);

        public string Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly StyledProperty<string> NoteProperty =
            AvaloniaProperty.Register<SectionHeader, string>(nameof(Note), string.Empty);

        /// <summary>
        /// Optional muted qualifier shown after the title, e.g. "· required".
        /// Hidden entirely when empty.
        /// </summary>
        public string Note
        {
            get => GetValue(NoteProperty);
            set => SetValue(NoteProperty, value);
        }

        public SectionHeader() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    }
}
