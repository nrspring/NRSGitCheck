using System.Collections.Generic;
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

    /// <summary>
    /// Eager convenience over <see cref="BuildDiffStream"/>: drains the lazy hunk
    /// stream into a fully-materialized <see cref="DiffDocument"/> for callers that
    /// want the whole diff in one object rather than progressively.
    /// </summary>
    public DiffDocument BuildDiff(string baseCommitSha, FileChange change, int contextLines = 3, bool wholeFile = false)
    {
        var stream = BuildDiffStream(baseCommitSha, change, contextLines, wholeFile);
        if (stream.IsBinary)
            return DiffDocument.Binary();
        if (stream.IsTooLarge)
            return DiffDocument.TooLarge();

        var hunks = new List<DiffHunk>();
        var added = 0;
        var removed = 0;
        foreach (var hunk in stream.Hunks)
        {
            hunks.Add(hunk);
            foreach (var line in hunk.Lines)
            {
                if (line.Kind == DiffLineKind.Added) added++;
                else if (line.Kind == DiffLineKind.Removed) removed++;
            }
        }

        return new DiffDocument { Hunks = hunks, LinesAdded = added, LinesRemoved = removed };
    }

    public DiffStream BuildDiffStream(string baseCommitSha, FileChange change, int contextLines = 3, bool wholeFile = false)
    {
        if (change.IsBinary)
            return DiffStream.Binary();

        var content = _git.GetFileContent(baseCommitSha, change);
        if (content.IsBinary)
            return DiffStream.Binary();

        if (CountLines(content.OldText) > MaxDiffLines || CountLines(content.NewText) > MaxDiffLines)
            return DiffStream.TooLarge();

        // Highlighting must see the whole file (multi-line grammar state), so it is
        // resolved up front; the slow part — the diff — then streams hunk by hunk.
        var oldColors = _highlighter.Highlight(change.Path, content.OldText);
        var newColors = _highlighter.Highlight(change.Path, content.NewText);

        return new DiffStream
        {
            Hunks = HighlightedHunks(content, contextLines, wholeFile, oldColors, newColors),
        };
    }

    private static IEnumerable<DiffHunk> HighlightedHunks(
        FileContent content, int contextLines, bool wholeFile,
        IReadOnlyList<IReadOnlyList<ColorSpan>>? oldColors,
        IReadOnlyList<IReadOnlyList<ColorSpan>>? newColors)
    {
        foreach (var hunk in DiffEngine.ComputeHunkStream(content.OldText, content.NewText, contextLines, wholeFile))
        {
            if (oldColors is not null || newColors is not null)
            {
                foreach (var line in hunk.Lines)
                {
                    if (line.Kind == DiffLineKind.Removed)
                    {
                        if (oldColors is not null && line.OldLineNumber is { } o && o - 1 < oldColors.Count)
                            line.Foreground = oldColors[o - 1];
                    }
                    else if (newColors is not null && line.NewLineNumber is { } nn && nn - 1 < newColors.Count)
                    {
                        line.Foreground = newColors[nn - 1];
                    }
                }
            }

            yield return hunk;
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
