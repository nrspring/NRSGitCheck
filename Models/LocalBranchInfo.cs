using System;

namespace NRSGitCheck.Models;

/// <summary>
/// One local branch as the branch list shows it. Everything here comes from the
/// repository itself, so it is available whether or not the GitHub CLI is.
/// </summary>
/// <param name="Name">The branch name, e.g. <c>feature/login</c>.</param>
/// <param name="LastCommitDate">Committer date of the branch tip; null for a branch with no commits.</param>
/// <param name="IsCurrent">Whether this branch is checked out.</param>
/// <param name="IsMainBranch">Whether this is the repository's main/master branch.</param>
/// <param name="IsMergedIntoMain">
/// Every commit on the branch is already reachable from main (local or <c>origin</c>).
/// True after a merge-commit or fast-forward merge; a squash or rebase merge
/// rewrites the commits, so it does not show up here — only the GitHub CLI can tell.
/// </param>
/// <param name="IsUpstreamGone">
/// The branch tracked a remote branch that no longer exists — usually because the
/// pull request was merged and its branch deleted. Only as current as the last fetch.
/// </param>
public sealed record LocalBranchInfo(
    string Name,
    DateTimeOffset? LastCommitDate,
    bool IsCurrent,
    bool IsMainBranch,
    bool IsMergedIntoMain,
    bool IsUpstreamGone);
