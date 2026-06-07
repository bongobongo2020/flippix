namespace FlipPix.UI.Models
{
    /// <summary>
    /// A selectable system-prompt template for Seedhunt's image analysis. The chosen template's
    /// markdown file (under prompts/prompt2json) is sent to llama-server as the system prompt so
    /// the generated video prompt is guided by elements from the analyzed image.
    /// </summary>
    public sealed class SeedHuntPromptTemplate
    {
        public SeedHuntPromptTemplate(string displayName, string fileName, string userInstruction)
        {
            DisplayName = displayName;
            FileName = fileName;
            UserInstruction = userInstruction;
        }

        /// <summary>Label shown in the picker.</summary>
        public string DisplayName { get; }

        /// <summary>Markdown file name under prompts/prompt2json (copied next to the exe at build).</summary>
        public string FileName { get; }

        /// <summary>The user-role message paired with the system prompt for this template.</summary>
        public string UserInstruction { get; }

        public override string ToString() => DisplayName;
    }
}
