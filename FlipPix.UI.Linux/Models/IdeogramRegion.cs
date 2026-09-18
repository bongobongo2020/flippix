using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// One composition region drawn on the Ideogram bbox canvas.
    /// Coordinates are stored in PIXELS relative to the editor canvas (see
    /// IdeogramViewModel.CanvasWidth/CanvasHeight) so they bind directly to
    /// Canvas.Left/Top + Width/Height. They are normalized to 0..1 at
    /// workflow-build time for the Ideogram4PromptBuilderKJ "elements_data" input.
    /// </summary>
    public partial class IdeogramRegion : ObservableObject
    {
        /// <summary>Canvas.Left in editor pixels.</summary>
        [ObservableProperty]
        private double _x;

        /// <summary>Canvas.Top in editor pixels.</summary>
        [ObservableProperty]
        private double _y;

        [ObservableProperty]
        private double _width = 120;

        [ObservableProperty]
        private double _height = 120;

        /// <summary>Per-region description that becomes the region's "desc" in elements_data.</summary>
        [ObservableProperty]
        private string _description = string.Empty;

        /// <summary>
        /// Element kind understood by Ideogram4PromptBuilderKJ: "obj" for a subject,
        /// "text" for rendered typography (which also carries <see cref="Text"/>).
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTextElement))]
        private string _type = "obj";

        /// <summary>Literal string to render when <see cref="Type"/> is "text".</summary>
        [ObservableProperty]
        private string _text = string.Empty;

        [JsonIgnore]
        public bool IsTextElement => Type == "text";

        /// <summary>
        /// Dominant hex colors for this region (becomes the region's "palette" in
        /// elements_data). Populated by the LLM analysis; empty when drawn by hand.
        /// </summary>
        public List<string> Palette { get; set; } = new();

        [ObservableProperty]
        private bool _isSelected;

        /// <summary>1-based label shown on the box overlay; assigned by the view model.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        private int _index = 1;

        [JsonIgnore]
        public string Label => Index.ToString();
    }
}
