using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using NRSGitCheck.Models;
using GitFileStatus = LibGit2Sharp.FileStatus;
using UiRepositoryStatus = NRSGitCheck.Models.RepositoryStatus;

namespace NRSGitCheck.Services;

/// <summary>
/// LibGit2Sharp-backed <see cref="IRepositoryStatusService"/>. Unlike
/// <see cref="GitService"/> it holds no state between calls: each read opens the
/// repository, snapshots it, and disposes the handle, which keeps concurrent reads
/// of different repositories safe and stops a long-lived handle from caching stale
/// refs after a checkout or pull.
/// </summary>
public sealed class RepositoryStatusService : IRepositoryStatusService
{
    /// <summary>
    /// Untracked directories are reported as a single entry rather than walked. The
    /// tab only needs "is there uncommitted work", and recursing can be slow in a
    /// repository with a large ignored-but-not-quite build output tree.
    /// </summary>
    private static readonly StatusOptions StatusOptions = new()
    {
        IncludeUntracked = true,
        RecurseUntrackedDirs = false,
        IncludeIgnored = false,
        ExcludeSubmodules = true,
    };

    public UiRepositoryStatus Read(string path)
    {
        var name = DeriveName(path);

        if (string.IsNullOrWhiteSpace(path))
            return UiRepositoryStatus.Failed(path ?? string.Empty, name, "No path.");

        if (!Directory.Exists(path))
            return UiRepositoryStatus.Failed(path, name, "Folder is missing.");

        try
        {
            var discovered = Repository.Discover(path);
            if (string.IsNullOrEmpty(discovered))
                return UiRepositoryStatus.Failed(path, name, "Not a Git repository.");

            using var repo = new Repository(discovered);
            return Snapshot(repo, path, name);
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            return UiRepositoryStatus.Failed(path, name, ex.Message);
        }
    }

    public async Task<IReadOnlyList<UiRepositoryStatus>> ReadAllAsync(
        IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths.ToList();

        // Each read owns its handle, so they can genuinely run side by side; a slow
        // repository then costs the refresh its own time rather than everyone's.
        var reads = list.Select(p => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Read(p);
        }, ct));

        return await Task.WhenAll(reads);
    }

    private static UiRepositoryStatus Snapshot(Repository repo, string path, string name)
    {
        var isUnborn = repo.Info.IsHeadUnborn;
        var isDetached = repo.Info.IsHeadDetached;
        var head = repo.Head;

        var branch = isDetached ? "(detached HEAD)" : head.FriendlyName;

        var locals = repo.Branches
            .Where(b => !b.IsRemote)
            .Select(b => b.FriendlyName)
            .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var status = repo.RetrieveStatus(StatusOptions);
        var uncommitted = status.Count(e =>
            e.State != GitFileStatus.Unaltered &&
            !e.State.HasFlag(GitFileStatus.Ignored));

        // TrackingDetails is only meaningful for a branch with an upstream; an
        // unborn or detached head has none, and neither does a never-pushed branch.
        var hasUpstream = !isUnborn && !isDetached && head.IsTracking;
        var ahead = hasUpstream ? head.TrackingDetails.AheadBy ?? 0 : 0;
        var behind = hasUpstream ? head.TrackingDetails.BehindBy ?? 0 : 0;

        var main = MainBranchDetector.Detect(repo)?.FriendlyName;
        var hasRemote = repo.Network.Remotes.Any();

        return new UiRepositoryStatus(
            path, name, IsValid: true, Error: null, branch, isDetached, isUnborn,
            locals, main, uncommitted, hasUpstream, ahead, behind, hasRemote);
    }

    private static string DeriveName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            var name = new DirectoryInfo(path).Name;
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return path;
        }
    }
}
