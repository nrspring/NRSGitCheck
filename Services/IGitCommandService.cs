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

    /// <summary>
    /// Creates a branch at the current HEAD and checks it out (<c>git checkout -b</c>).
    /// Git rejects a name that already exists or is not a legal ref, and that refusal
    /// comes back as a failed result.
    /// </summary>
    Task<GitCommandResult> CreateBranchAsync(
        string workingDirectory, string branch, CancellationToken ct = default);

    /// <summary>
    /// Sends the checked-out branch's commits to the remote. When
    /// <paramref name="setUpstream"/> is set the branch does not exist on the remote
    /// yet, so this publishes it to <c>origin</c> and starts tracking it
    /// (<c>git push --set-upstream origin branch</c>); otherwise it is a plain
    /// <c>git push</c> to wherever the branch already tracks. Never forces: a push
    /// the remote rejects because it has commits this clone lacks comes back as a
    /// failed result carrying Git's own words, not as a rewritten remote branch.
    /// </summary>
    Task<GitCommandResult> PushAsync(
        string workingDirectory, string branch, bool setUpstream, CancellationToken ct = default);

    /// <summary>
    /// Stages everything in the working tree (<c>git add -A</c>) and commits it with
    /// the given message. Anything Git refuses — an empty message, no configured
    /// identity, a failing pre-commit hook — comes back as a failed result with
    /// Git's own words; nothing is retried with a hook bypassed.
    /// </summary>
    Task<GitCommandResult> CommitAllAsync(
        string workingDirectory, string message, CancellationToken ct = default);

    /// <summary>
    /// Throws the working tree away: <c>git reset --hard</c> puts every tracked file
    /// back to the checked-out commit, and when
    /// <paramref name="deleteUntrackedFiles"/> is set, <c>git clean -fd</c> then
    /// deletes files Git does not track. Ignored files (build output, local config)
    /// are never touched. This is not recoverable through Git, so only call it
    /// behind an explicit confirmation.
    /// </summary>
    Task<GitCommandResult> DiscardChangesAsync(
        string workingDirectory, bool deleteUntrackedFiles, CancellationToken ct = default);
}
