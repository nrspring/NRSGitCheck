using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// LibGit2Sharp-backed <see cref="IGitService"/>. Holds a single open repository;
/// access is serialized with a lock because a LibGit2Sharp <see cref="Repository"/>
/// is not thread-safe and callers invoke it from background threads.
/// </summary>
public sealed class GitService : IGitService, IDisposable
{
    private readonly object _gate = new();
    private Repository? _repo;

    public RepositorySnapshot OpenRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new GitException("No folder was selected.");

        string? discovered;
        try
        {
            discovered = Repository.Discover(path);
        }
        catch (Exception ex)
        {
            throw new GitException($"Could not read '{path}': {ex.Message}");
        }

        if (string.IsNullOrEmpty(discovered))
            throw new GitException($"'{path}' is not inside a Git repository.");

        lock (_gate)
        {
            _repo?.Dispose();
            _repo = new Repository(discovered);
            return BuildSnapshot(_repo);
        }
    }

    public ResolvedComparison ResolveComparison(ComparisonMode mode, string? otherBranch, string? parentBranch)
    {
        lock (_gate)
        {
            var repo = _repo ?? throw new GitException("No repository is open.");

            if (repo.Info.IsHeadUnborn)
                return ResolvedComparison.Unresolved("The current branch has no commits yet.");

            switch (mode)
            {
                case ComparisonMode.LastCommit:
                {
                    var tip = repo.Head.Tip;
                    return ResolvedComparison.Resolved(tip.Sha, $"last commit ({Shorten(tip.Sha)})");
                }

                case ComparisonMode.OtherBranch:
                {
                    if (string.IsNullOrWhiteSpace(otherBranch))
                        return ResolvedComparison.Unresolved("Select a branch to compare against.");

                    var branch = FindLocalBranch(repo, otherBranch);
                    if (branch?.Tip is null)
                        return ResolvedComparison.Unresolved($"Branch '{otherBranch}' was not found.");

                    return ResolvedComparison.Resolved(branch.Tip.Sha, $"{otherBranch} ({Shorten(branch.Tip.Sha)})");
                }

                case ComparisonMode.BranchBase:
                {
                    if (string.IsNullOrWhiteSpace(parentBranch))
                        return ResolvedComparison.Unresolved("Select the parent branch to find the branch base.");

                    var parent = FindLocalBranch(repo, parentBranch);
                    if (parent?.Tip is null)
                        return ResolvedComparison.Unresolved($"Branch '{parentBranch}' was not found.");

                    var baseCommit = repo.ObjectDatabase.FindMergeBase(repo.Head.Tip, parent.Tip);
                    if (baseCommit is null)
                        return ResolvedComparison.Unresolved($"No common history with '{parentBranch}'.");

                    return ResolvedComparison.Resolved(baseCommit.Sha, $"base with {parentBranch} ({Shorten(baseCommit.Sha)})");
                }

                default:
                    return ResolvedComparison.Unresolved("Unknown comparison mode.");
            }
        }
    }

    private static RepositorySnapshot BuildSnapshot(Repository repo)
    {
        var workdir = (repo.Info.WorkingDirectory ?? repo.Info.Path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = new DirectoryInfo(workdir).Name;

        var isUnborn = repo.Info.IsHeadUnborn;
        var isDetached = repo.Info.IsHeadDetached;
        var currentBranch = isDetached ? "(detached HEAD)"
            : isUnborn ? repo.Head.FriendlyName
            : repo.Head.FriendlyName;
        var headShort = isUnborn ? "" : Shorten(repo.Head.Tip.Sha);

        var locals = repo.Branches
            .Where(b => !b.IsRemote)
            .Select(b => new BranchInfo(
                b.FriendlyName,
                b.Tip?.Sha ?? "",
                Shorten(b.Tip?.Sha),
                b.IsCurrentRepositoryHead))
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var parent = DetectDefaultParent(repo, currentBranch, locals);

        return new RepositorySnapshot(
            workdir, name, currentBranch, isDetached, isUnborn, headShort, locals, parent);
    }

    /// <summary>
    /// Best-effort guess of the branch the current branch was based on (FR-9):
    /// the tracked upstream's local name if present, otherwise a conventional
    /// integration branch. Returns null if nothing sensible is found, in which
    /// case the UI asks the user to choose.
    /// </summary>
    private static string? DetectDefaultParent(Repository repo, string currentBranch, IReadOnlyList<BranchInfo> locals)
    {
        var tracked = repo.Head.TrackedBranch;
        if (tracked is not null)
        {
            var friendly = tracked.FriendlyName;            // e.g. "origin/main"
            var slash = friendly.IndexOf('/');
            var localName = slash >= 0 ? friendly[(slash + 1)..] : friendly;
            if (!string.Equals(localName, currentBranch, StringComparison.Ordinal) &&
                locals.Any(b => b.Name == localName))
                return localName;
        }

        foreach (var candidate in new[] { "main", "master", "develop" })
        {
            if (!string.Equals(candidate, currentBranch, StringComparison.Ordinal) &&
                locals.Any(b => b.Name == candidate))
                return candidate;
        }

        return null;
    }

    private static Branch? FindLocalBranch(Repository repo, string name) =>
        repo.Branches.FirstOrDefault(b => !b.IsRemote && b.FriendlyName == name);

    private static string Shorten(string? sha) =>
        string.IsNullOrEmpty(sha) ? "" : sha.Length <= 7 ? sha : sha[..7];

    public void Dispose()
    {
        lock (_gate)
        {
            _repo?.Dispose();
            _repo = null;
        }
    }
}
