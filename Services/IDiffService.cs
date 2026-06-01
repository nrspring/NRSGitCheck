using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Produces the rendered <see cref="DiffDocument"/> for a changed file, handling
/// binary and oversized files (FR-17, FR-22).
/// </summary>
public interface IDiffService
{
    DiffDocument BuildDiff(string baseCommitSha, FileChange change, int contextLines = 3);
}
