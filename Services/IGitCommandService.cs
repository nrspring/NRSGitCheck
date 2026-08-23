using System.Threading;
using System.Threading.Tasks;

namespace NRSGitCheck.Services;

/// <summary>Outcome of a Git command, with a message suitable for the status bar.</summary>
public sealed record GitCommandResult(bool Success, string Message);

/// <summary>
/// The one place the application is allowed to *write* to a repository. Kept apart
/// from the strictly read-only <see cref="IGitService"/> so that contract is not
/// weakened: nothing here runs unless the user presses Pull main.
/// </summary>
public interface IGitCommandService
{
    /// <summary>
    /// Brings the repository's main branch up to date with its remote. When main is
    /// not the checked-out branch this only moves the local ref (a fast-forward
    /// fetch), so the working tree is never touched; when main *is* checked out it
    /// fast-forwards it. Never merges, rebases, or force-updates — a diverged main
    /// comes back as a failed result rather than a rewritten branch.
    /// </summary>
    Task<GitCommandResult> PullMainAsync(
        string workingDirectory, string? mainBranch, string? currentBranch, CancellationToken ct = default);
}
