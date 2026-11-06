using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One character on the 🔁 Character Swap tab: the photo that replaces a person, and optionally the
    /// role of the person it replaces ("the drummer"). Simple mode uses slot 1 only; Advanced uses up to four.
    /// </summary>
    public partial class CharacterSwapSlot : ObservableObject
    {
        public CharacterSwapSlot(int number, Action<CharacterSwapSlot> browse)
        {
            Number = number;
            BrowseCommand = new RelayCommand(() => browse(this));
            ClearCommand = new RelayCommand(() => PicturePath = string.Empty, () => HasPicture);
        }

        public int Number { get; }
        public string Label => $"Character {Number}";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPicture), nameof(FileName))]
        private string _picturePath = string.Empty;

        /// <summary>
        /// Who this character replaces, by role, never by clothes: clothing words leak into the result.
        /// In Advanced mode a look can follow " - " to help match people; only the part before it reaches H3.
        /// </summary>
        [ObservableProperty]
        private string _role = string.Empty;

        public bool HasPicture => !string.IsNullOrEmpty(PicturePath) && File.Exists(PicturePath);
        public string FileName => HasPicture ? Path.GetFileName(PicturePath) : "No picture";

        private BitmapImage? _thumbnail;

        /// <summary>Decoded once per picture, on a worker thread. (It used to decode the file on every read
        /// of the property, on the UI thread.)</summary>
        public BitmapImage? Thumbnail => _thumbnail;

        partial void OnPicturePathChanged(string value)
        {
            ClearCommand.NotifyCanExecuteChanged();
            _thumbnail = null;
            OnPropertyChanged(nameof(Thumbnail));
            FlipPix.UI.Services.AsyncThumbnail.Load(value, 240, 0, bitmap =>
            {
                if (PicturePath != value) return;
                _thumbnail = bitmap;
                OnPropertyChanged(nameof(Thumbnail));
            });
        }

        public RelayCommand BrowseCommand { get; }
        public RelayCommand ClearCommand { get; }
    }
}
