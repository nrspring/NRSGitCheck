using System.Threading;
using System.Threading.Tasks;
using NRSGitCheck.Models;

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

    /// <summary>
    /// Fetches a pull request's head into a local <c>pr-N</c> branch and checks it
    /// out. Like <see cref="PullMainAsync"/> this is fast-forward only: if a
    /// <c>pr-N</c> branch already exists and has diverged (a force-push, or local
    /// commits), the fetch is refused rather than rewritten. The checkout is a plain
    /// one, so Git itself refuses to proceed when uncommitted work would be lost.
    /// </summary>
    Task<GitCommandResult> CheckoutPullRequestAsync(
        string workingDirectory, PullRequestReference pr, string? currentBranch, CancellationToken ct = default);

    /// <summary>
    /// Checks out an existing local branch. This is a plain checkout: Git carries
    /// uncommitted changes across when it safely can, and refuses — returning its
    /// own message as a failed result — when the switch would overwrite them.
    /// Nothing is stashed, merged, or discarded on the user's behalf.
    /// </summary>
    Task<GitCommandResult> CheckoutBranchAsync(
        string workingDirectory, string branch, CancellationToken ct = default);
}
