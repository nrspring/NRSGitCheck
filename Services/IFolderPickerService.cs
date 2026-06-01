using System.Threading.Tasks;

namespace NRSGitCheck.Services;

/// <summary>
/// Abstracts the native folder-picker so view models stay free of UI types (FR-1).
/// </summary>
public interface IFolderPickerService
{
    /// <summary>Shows a folder picker; returns the chosen local path, or null if cancelled.</summary>
    Task<string?> PickFolderAsync(string title);
}
