using System.Threading.Tasks;

namespace NRSGitCheck.Services;

/// <summary>
/// Abstracts the system clipboard so view models stay free of UI types, in the same
/// way <see cref="IFolderPickerService"/> does for the folder picker.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Puts text on the clipboard. Returns false when there is nothing to copy or no
    /// window to copy through, rather than throwing.
    /// </summary>
    Task<bool> SetTextAsync(string? text);
}
