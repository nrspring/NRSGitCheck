using System;
using System.Collections.Generic;

namespace NRSGitCheck.Models;

/// <summary>
/// A point-in-time, read-only summary of one tracked repository, as shown in the
/// Repositories tab. Built on a background thread from its own Git handle, so it
/// carries only plain values and is safe to hand to view models.
/// </summary>
public sealed record RepositoryStatus(
    string Path,
    string Name,
    bool IsValid,
    string? Error,
    string CurrentBranch,
    bool IsDetachedHead,
    bool IsHeadUnborn,
    IReadOnlyList<string> LocalBranches,
    string? MainBranch,
    int UncommittedCount,
    bool HasUpstream,
    int AheadBy,
    int BehindBy,
    bool HasRemote)
{
    /// <summary>Working-tree or index changes, including untracked files.</summary>
    public bool HasUncommittedChanges => UncommittedCount > 0;

    /// <summary>Commits on the current branch that its upstream does not have.</summary>
    public bool HasUnpushedCommits => AheadBy > 0;

    /// <summary>
    /// The local branch name to check out for "switch to main" — for a repository
    /// that only has <c>origin/main</c>, the local branch Git would create for it.
    /// </summary>
    public string? LocalMainBranch
    {
        get
        {
            if (string.IsNullOrEmpty(MainBranch))
                return null;

            var slash = MainBranch.IndexOf('/');
            return slash < 0 ? MainBranch : MainBranch[(slash + 1)..];
        }
    }

    /// <summary>Whether main is the branch currently checked out (FR: pull-all targets these).</summary>
    public bool IsOnMainBranch =>
        !IsDetachedHead &&
        LocalMainBranch is { Length: > 0 } main &&
        string.Equals(CurrentBranch, main, StringComparison.Ordinal);

    /// <summary>A status for a path that could not be read as a Git repository.</summary>
    public static RepositoryStatus Failed(string path, string name, string error) => new(
        path, name, IsValid: false, error, CurrentBranch: string.Empty,
        IsDetachedHead: false, IsHeadUnborn: false, LocalBranches: Array.Empty<string>(),
        MainBranch: null, UncommittedCount: 0, HasUpstream: false, AheadBy: 0, BehindBy: 0,
        HasRemote: false);
}
