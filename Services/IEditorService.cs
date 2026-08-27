namespace NRSGitCheck.Services;

/// <summary>Outcome of handing a folder to the external editor.</summary>
public sealed record EditorResult(bool Success, string Message);

/// <summary>
/// Opens a repository folder in the user's code editor. Kept behind an interface so
/// view models never touch <see cref="System.Diagnostics.Process"/>, and so tests can
/// assert what would have been launched without launching it.
/// </summary>
public interface IEditorService
{
    /// <summary>What to call the editor in buttons and status messages.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the editor was found on this machine. False disables the button rather
    /// than letting the user press something that can only fail.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Launches the editor on <paramref name="folder"/> and returns once it has been
    /// started — not once the user closes it. A missing editor or a folder that is no
    /// longer there comes back as a failed result rather than an exception.
    /// </summary>
    EditorResult Open(string folder);
}
