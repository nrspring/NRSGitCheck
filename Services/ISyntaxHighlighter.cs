using System.Collections.Generic;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Produces per-line syntax-highlight color spans for a file's text (FR-20).
/// </summary>
public interface ISyntaxHighlighter
{
    /// <summary>
    /// Tokenizes <paramref name="text"/> using the grammar inferred from
    /// <paramref name="filePath"/>'s extension. Returns one span list per line
    /// (indexed identically to the diff engine's line splitting), or null when no
    /// grammar matches the file type.
    /// </summary>
    IReadOnlyList<IReadOnlyList<ColorSpan>>? Highlight(string filePath, string text);

    /// <summary>Switches the highlighting theme to match the app's light/dark mode (FR-20, Phase 7).</summary>
    void SetDark(bool isDark);
}
