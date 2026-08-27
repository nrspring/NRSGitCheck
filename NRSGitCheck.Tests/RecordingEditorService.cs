using System.Collections.Generic;
using NRSGitCheck.Services;

namespace NRSGitCheck.Tests;

/// <summary>
/// Stands in for the real editor launcher: records what a test would have opened
/// instead of starting a process. Shared by every test that builds a
/// <see cref="NRSGitCheck.ViewModels.RepositoriesViewModel"/>.
/// </summary>
internal sealed class RecordingEditorService : IEditorService
{
    /// <summary>Folders handed to <see cref="Open"/>, in order.</summary>
    public List<string> Opened { get; } = new();

    public string Name { get; set; } = "Visual Studio Code";

    /// <summary>Set false to model an editor that is not installed.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>Set to make the next open come back as a failure.</summary>
    public string? Refusal { get; set; }

    public EditorResult Open(string folder)
    {
        Opened.Add(folder);
        return Refusal is { } refusal
            ? new EditorResult(false, refusal)
            : new EditorResult(true, $"Opened in {Name}.");
    }
}
