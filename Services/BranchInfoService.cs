using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>LibGit2Sharp-backed <see cref="IBranchInfoService"/>.</summary>
public sealed class BranchInfoService : IBranchInfoService
{
    public IReadOnlyList<LocalBranchInfo> ReadLocalBranches(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            throw new DirectoryNotFoundException("The repository folder is missing.");

        var discovered = Repository.Discover(path);
        if (string.IsNullOrEmpty(discovered))
            throw new InvalidOperationException("Not a Git repository.");

        using var repo = new Repository(discovered);

        var main = MainBranchDetector.Detect(repo);
        var mainName = main is null ? null : LocalName(main);

        // "Merged" is judged against main both as this clone has it and as origin has
        // it: a branch merged on GitHub is in origin/main after a fetch, even when the
        // local main has not been pulled yet.
        var mainTips = new List<Commit>();
        if (main?.Tip is { } mainTip)
            mainTips.Add(mainTip);
        if (mainName is not null &&
            repo.Branches.FirstOrDefault(b => b.IsRemote && b.FriendlyName == $"origin/{mainName}")?.Tip is { } remoteTip &&
            mainTips.All(t => t.Sha != remoteTip.Sha))
            mainTips.Add(remoteTip);

        return repo.Branches
            .Where(b => !b.IsRemote)
            .Select(b =>
            {
                var isMain = string.Equals(b.FriendlyName, mainName, StringComparison.Ordinal);
                return new LocalBranchInfo(
                    b.FriendlyName,
                    b.Tip?.Committer.When,
                    b.IsCurrentRepositoryHead,
                    isMain,
                    IsMergedIntoMain: !isMain && b.Tip is { } tip && mainTips.Any(m => IsReachable(repo, tip, m)),
                    IsUpstreamGone: IsUpstreamGone(b));
            })
            .OrderByDescending(b => b.LastCommitDate ?? DateTimeOffset.MinValue)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Whether <paramref name="tip"/> is already in the history of <paramref name="target"/>.</summary>
    private static bool IsReachable(Repository repo, Commit tip, Commit target) =>
        tip.Sha == target.Sha || repo.ObjectDatabase.FindMergeBase(tip, target)?.Sha == tip.Sha;

    /// <summary>
    /// The branch is configured to track a remote branch whose ref is no longer there
    /// — what <c>git branch -vv</c> calls "gone".
    /// </summary>
    private static bool IsUpstreamGone(Branch branch) =>
        !string.IsNullOrEmpty(branch.UpstreamBranchCanonicalName) &&
        branch.TrackedBranch?.Tip is null;

    private static string LocalName(Branch branch)
    {
        if (!branch.IsRemote)
            return branch.FriendlyName;

        var slash = branch.FriendlyName.IndexOf('/');
        return slash < 0 ? branch.FriendlyName : branch.FriendlyName[(slash + 1)..];
    }
}
