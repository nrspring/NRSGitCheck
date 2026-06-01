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

    public DiffService(IGitService git) => _git = git;

    public DiffDocument BuildDiff(string baseCommitSha, FileChange change, int contextLines = 3)
    {
        if (change.IsBinary)
            return DiffDocument.Binary();

        var content = _git.GetFileContent(baseCommitSha, change);
        if (content.IsBinary)
            return DiffDocument.Binary();

        if (CountLines(content.OldText) > MaxDiffLines || CountLines(content.NewText) > MaxDiffLines)
            return DiffDocument.TooLarge();

        return DiffEngine.Compute(content.OldText, content.NewText, contextLines);
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
