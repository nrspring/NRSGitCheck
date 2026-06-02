using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Produces the rendered <see cref="DiffDocument"/> for a changed file, handling
/// binary and oversized files (FR-17, FR-22).
/// </summary>
public interface IDiffService
{
    /// <param name="wholeFile">
    /// When true, render the entire file on both sides instead of just changed
    /// regions with surrounding context, keeping the diff highlighting intact.
    /// </param>
    DiffDocument BuildDiff(string baseCommitSha, FileChange change, int contextLines = 3, bool wholeFile = false);
}
