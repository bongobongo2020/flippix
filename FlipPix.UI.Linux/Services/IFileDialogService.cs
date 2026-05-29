using System.Threading.Tasks;

namespace FlipPix.UI.Linux.Services
{
    public interface IFileDialogService
    {
        Task<string?> OpenFileDialogAsync(string title, string filter, string? initialDirectory = null);
        Task<string[]> OpenFilesDialogAsync(string title, string filter, string? initialDirectory = null);
        Task<string?> SaveFileDialogAsync(string title, string filter, string defaultFileName, string? initialDirectory = null);
        Task<string?> OpenFolderDialogAsync(string title, string? initialDirectory = null, bool showNewFolderButton = false);
    }
}
