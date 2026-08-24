using System;
using System.Linq;
using LibGit2Sharp;

namespace NRSGitCheck.Services;

/// <summary>
/// Finds a repository's integration branch. Shared by the diff-side
/// <see cref="GitService"/> and the multi-repo <see cref="RepositoryStatusService"/>
/// so both agree on what "main" means.
/// </summary>
internal static class MainBranchDetector
{
    /// <summary>The branch names treated as a repository's integration branch, in preference order.</summary>
    private static readonly string[] Candidates = { "main", "master" };

    /// <summary>
    /// A local <c>main</c>/<c>master</c> if there is one, otherwise the
    /// remote-tracking equivalent so callers still have a target in a repository
    /// that has never checked main out locally. Null if neither exists.
    /// </summary>
    internal static Branch? Detect(Repository repo)
    {
        foreach (var name in Candidates)
            if (FindLocal(repo, name) is { } local)
                return local;

        foreach (var name in Candidates)
            if (repo.Branches.FirstOrDefault(b => b.IsRemote && b.FriendlyName == $"origin/{name}") is { } remote)
                return remote;

        return null;
    }

    /// <summary>Whether a branch name is one this application treats as "main".</summary>
    internal static bool IsIntegrationBranchName(string? name) =>
        name is not null && Array.Exists(Candidates, c => string.Equals(c, name, StringComparison.Ordinal));

    internal static Branch? FindLocal(Repository repo, string name) =>
        repo.Branches.FirstOrDefault(b => !b.IsRemote && b.FriendlyName == name);
}
