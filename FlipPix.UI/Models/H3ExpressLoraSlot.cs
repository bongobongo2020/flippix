using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One row of ⚡ H3 Express's LoRA stack: a LoRA from <c>loras/H3</c> and the model strength it is
    /// loaded at. The rows are chained onto the checkpoint in list order, so the row above is the wire the
    /// row below reads.
    ///
    /// <para>The row owns no commands of its own: ✕ is bound to the tab's remove command with the row as its
    /// parameter, so the list stays a plain model and the same template serves the rail and the ➕ New job
    /// sheet.</para>
    ///
    /// <para>The sibling of <see cref="MiniMaxI2VLoraSlot"/>, which is the same row on 🌀 MiniMax I2V. They
    /// are kept apart on purpose: each tab persists its stack under its own settings key, and a shared row
    /// type would invite one tab's list to be handed to the other's builder.</para>
    /// </summary>
    public partial class H3ExpressLoraSlot : ObservableObject
    {
        private string _name = string.Empty;
        private double _strength = 1.0;
        private int _index;

        public H3ExpressLoraSlot(int index, string name = "", double strength = 1.0)
        {
            _index = index;
            _name = Normalize(name);
            _strength = Math.Clamp(strength, MinStrength, MaxStrength);
        }

        public const double MinStrength = 0.0;
        public const double MaxStrength = 2.0;

        /// <summary>1-based position in the stack; renumbered when an earlier row is removed.</summary>
        public int Index
        {
            get => _index;
            set { if (_index == value) return; _index = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); }
        }

        public string Title => $"LoRA {Index}";

        /// <summary>As ComfyUI names it — <c>H3/….safetensors</c> — or empty for a row that loads nothing.</summary>
        public string Name
        {
            get => _name;
            set
            {
                var name = Normalize(value);
                if (_name == name) return;
                _name = name;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLora));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool HasLora => _name.Length > 0;

        /// <summary><c>strength_model</c>. 0 leaves the row out of the submitted graph entirely.</summary>
        public double Strength
        {
            get => _strength;
            set
            {
                var v = Math.Clamp(Math.Round(value, 2), MinStrength, MaxStrength);
                if (Math.Abs(_strength - v) < 0.0001) return;
                _strength = v;
                OnPropertyChanged();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Whether this row actually reaches the graph: something chosen, at a strength above 0.</summary>
        public bool IsActive => HasLora && _strength > 0.0;

        /// <summary>Raised when the row changes, so the owner can re-summarise and persist.</summary>
        public event EventHandler? Changed;

        private static string Normalize(string? name) => (name ?? string.Empty).Trim().Replace('\\', '/');
    }
}
