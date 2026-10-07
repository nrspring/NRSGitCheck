using System.Threading;
using System.Threading.Tasks;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Pull request information through the GitHub CLI (<c>gh</c>). The CLI is used, as
/// <c>git</c> is, so the application never handles a GitHub token itself: whoever is
/// signed in to <c>gh</c> is who the requests run as. Everything here is read-only.
/// </summary>
public interface IGitHubCliService
{
    /// <summary>
    /// Looks for <c>gh</c> and asks it whether it is signed in. Safe to call again
    /// after the user installs or signs in — nothing is cached between calls.
    /// </summary>
    Task<GitHubCliStatus> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// The repository's recent pull requests in every state, as <c>gh pr list</c>
    /// sees them from that working directory. A repository whose remote is not on
    /// GitHub, or a network failure, comes back as a failed result with gh's message.
    /// </summary>
    Task<PullRequestListResult> ListPullRequestsAsync(string workingDirectory, CancellationToken ct = default);
}
