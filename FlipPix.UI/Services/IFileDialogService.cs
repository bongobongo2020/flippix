using System.Threading.Tasks;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Service for file dialog operations, enabling better testability
    /// and separation of concerns from ViewModels.
    /// </summary>
    public interface IFileDialogService
    {
        /// <summary>
        /// Opens a file dialog for selecting a single file.
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="filter">File filter (e.g., "Image Files|*.jpg;*.png|All Files|*.*")</param>
        /// <param name="initialDirectory">Starting directory (optional)</param>
        /// <returns>Selected file path, or null if cancelled</returns>
        Task<string?> OpenFileDialogAsync(string title, string filter, string? initialDirectory = null);

        /// <summary>
        /// Opens a file dialog for selecting multiple files.
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="filter">File filter</param>
        /// <param name="initialDirectory">Starting directory (optional)</param>
        /// <returns>Array of selected file paths, or empty array if cancelled</returns>
        Task<string[]> OpenFilesDialogAsync(string title, string filter, string? initialDirectory = null);

        /// <summary>
        /// Opens a save file dialog.
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="filter">File filter</param>
        /// <param name="defaultFileName">Suggested file name</param>
        /// <param name="initialDirectory">Starting directory (optional)</param>
        /// <returns>Selected file path, or null if cancelled</returns>
        Task<string?> SaveFileDialogAsync(string title, string filter, string defaultFileName, string? initialDirectory = null);

        /// <summary>
        /// Opens a folder browser dialog.
        /// </summary>
        /// <param name="title">Dialog title</param>
        /// <param name="initialDirectory">Starting directory (optional)</param>
        /// <param name="showNewFolderButton">Whether to show "New Folder" button</param>
        /// <returns>Selected folder path, or null if cancelled</returns>
        Task<string?> OpenFolderDialogAsync(string title, string? initialDirectory = null, bool showNewFolderButton = false);
    }
}
