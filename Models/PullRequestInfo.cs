using System;
using System.Collections.Generic;
using System.Linq;

namespace NRSGitCheck.Models;

public enum PullRequestState
{
    Open,
    Closed,
    Merged,
}

/// <summary>A pull request as the GitHub CLI reports it.</summary>
/// <param name="HeadBranch">The branch the PR was opened from.</param>
/// <param name="IsCrossRepository">
/// Opened from a fork. Its head branch lives in someone else's repository, so a
/// local branch of the same name is a coincidence, not the PR's branch.
/// </param>
public sealed record PullRequestInfo(
    int Number,
    string Title,
    PullRequestState State,
    bool IsDraft,
    string HeadBranch,
    bool IsCrossRepository,
    string Url)
{
    /// <summary>
    /// The pull request a local branch belongs to, or null. A <c>pr-N</c> branch —
    /// what this app creates to review a PR — maps to PR N directly. Any other branch
    /// matches PRs opened from a branch of the same name in this repository; when a
    /// branch has had several (one closed, then reopened as a new PR), the newest wins.
    /// </summary>
    public static PullRequestInfo? FindFor(string branch, IEnumerable<PullRequestInfo> pullRequests)
    {
        if (PullRequestReference.TryGetNumberFromBranch(branch, out var number))
            return pullRequests.FirstOrDefault(pr => pr.Number == number);

        return pullRequests
            .Where(pr => !pr.IsCrossRepository && string.Equals(pr.HeadBranch, branch, StringComparison.Ordinal))
            .OrderByDescending(pr => pr.Number)
            .FirstOrDefault();
    }
}

/// <summary>What the app knows about the GitHub CLI on this machine.</summary>
public enum GitHubCliStatus
{
    /// <summary>Not checked yet.</summary>
    Unknown,

    /// <summary>Installed and signed in; pull request status is available.</summary>
    Available,

    /// <summary><c>gh</c> could not be found.</summary>
    NotInstalled,

    /// <summary><c>gh</c> runs, but <c>gh auth status</c> reports no usable login.</summary>
    NotSignedIn,
}

/// <summary>The outcome of listing a repository's pull requests.</summary>
public sealed record PullRequestListResult(bool Success, IReadOnlyList<PullRequestInfo> PullRequests, string? Error)
{
    public static PullRequestListResult Failed(string error) => new(false, Array.Empty<PullRequestInfo>(), error);
}
