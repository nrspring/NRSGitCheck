using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Read-only access to a Git repository (FR-1..2, FR-7..11). The service keeps a
/// single repository open; calls are serialized internally. It never writes to
/// the repository.
/// </summary>
public interface IGitService
{
    /// <summary>
    /// Opens and validates the repository at (or containing) <paramref name="path"/>,
    /// replacing any previously open one. Throws <see cref="GitException"/> with a
    /// user-facing message if the folder is not a valid Git working directory.
    /// </summary>
    RepositorySnapshot OpenRepository(string path);

    /// <summary>
    /// Resolves the chosen comparison mode to a concrete base commit against the
    /// currently open repository (FR-7..9). Does not throw for ordinary "can't
    /// resolve" cases — those come back as an unresolved <see cref="ResolvedComparison"/>.
    /// </summary>
    ResolvedComparison ResolveComparison(ComparisonMode mode, string? otherBranch, string? parentBranch);
}
