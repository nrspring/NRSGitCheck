using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Bridges Git content retrieval and the pure <see cref="DiffEngine"/>, applying
/// the binary short-circuit and large-file guard (FR-17, FR-22).
/// </summary>
public sealed class DiffService : IDiffService
{
    /// <summary>Above this many lines on either side, the diff is not rendered (FR-22).</summary>
    private const int MaxDiffLines = 20_000;

    private readonly IGitService _git;
    private readonly ISyntaxHighlighter _highlighter;

    public DiffService(IGitService git, ISyntaxHighlighter highlighter)
    {
        _git = git;
        _highlighter = highlighter;
    }

    public DiffDocument BuildDiff(string baseCommitSha, FileChange change, int contextLines = 3)
    {
        if (change.IsBinary)
            return DiffDocument.Binary();

        var content = _git.GetFileContent(baseCommitSha, change);
        if (content.IsBinary)
            return DiffDocument.Binary();

        if (CountLines(content.OldText) > MaxDiffLines || CountLines(content.NewText) > MaxDiffLines)
            return DiffDocument.TooLarge();

        var doc = DiffEngine.Compute(content.OldText, content.NewText, contextLines);
        ApplyHighlighting(doc, change.Path, content.OldText, content.NewText);
        return doc;
    }

    /// <summary>
    /// Tokenizes both sides and attaches the per-line foreground spans to each
    /// diff line, choosing the old or new tokenization by the line's role (FR-20).
    /// </summary>
    private void ApplyHighlighting(DiffDocument doc, string path, string oldText, string newText)
    {
        var oldColors = _highlighter.Highlight(path, oldText);
        var newColors = _highlighter.Highlight(path, newText);
        if (oldColors is null && newColors is null)
            return;

        foreach (var hunk in doc.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == DiffLineKind.Removed)
                {
                    if (oldColors is not null && line.OldLineNumber is { } o && o - 1 < oldColors.Count)
                        line.Foreground = oldColors[o - 1];
                }
                else if (newColors is not null && line.NewLineNumber is { } n && n - 1 < newColors.Count)
                {
                    line.Foreground = newColors[n - 1];
                }
            }
        }
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 1;
        foreach (var c in text)
            if (c == '\n')
                count++;
        return count;
    }
}
