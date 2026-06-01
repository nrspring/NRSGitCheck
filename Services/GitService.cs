using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using NRSGitCheck.Models;
using ChangeKind = NRSGitCheck.Models.ChangeKind;
using LibGitChangeKind = LibGit2Sharp.ChangeKind;

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

    public IReadOnlyList<FileChange> GetChanges(string baseCommitSha)
    {
        lock (_gate)
        {
            var repo = _repo ?? throw new GitException("No repository is open.");

            var commit = repo.Lookup<Commit>(baseCommitSha)
                ?? throw new GitException("The comparison base commit could not be found.");

            var result = new List<FileChange>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Files not under version control. The tree->workdir diff reports these
            // as "Added", so we use this set to reclassify them as Untracked (FR-13).
            var untrackedSet = new HashSet<string>(StringComparer.Ordinal);
            var status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
                IncludeIgnored = false,
            });
            foreach (var u in status.Untracked)
                untrackedSet.Add(u.FilePath);

            // Differences between the base tree and the working directory.
            var patch = repo.Diff.Compare<Patch>(commit.Tree, DiffTargets.WorkingDirectory);
            foreach (var entry in patch)
            {
                var kind = untrackedSet.Contains(entry.Path)
                    ? ChangeKind.Untracked
                    : MapStatus(entry.Status);

                result.Add(new FileChange(
                    entry.Path,
                    entry.OldPath != entry.Path ? entry.OldPath : null,
                    kind,
                    entry.LinesAdded,
                    entry.LinesDeleted,
                    entry.IsBinaryComparison));
                seen.Add(entry.Path);
            }

            // Safety net: include any untracked file the diff didn't surface.
            foreach (var path in untrackedSet)
            {
                if (!seen.Add(path))
                    continue;

                var (lines, isBinary) = CountWorkdirLines(repo, path);
                result.Add(new FileChange(path, null, ChangeKind.Untracked, lines, 0, isBinary));
            }

            return result
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public FileContent GetFileContent(string baseCommitSha, FileChange change)
    {
        lock (_gate)
        {
            var repo = _repo ?? throw new GitException("No repository is open.");
            var commit = repo.Lookup<Commit>(baseCommitSha)
                ?? throw new GitException("The comparison base commit could not be found.");

            var isBinary = false;
            string oldText = "";
            string newText = "";

            // Old side: from the base commit. For renames the content lives at OldPath.
            if (change.Kind is not (ChangeKind.Added or ChangeKind.Untracked))
            {
                var oldPath = change.OldPath ?? change.Path;
                if (commit[oldPath]?.Target is Blob blob)
                {
                    if (blob.IsBinary)
                        isBinary = true;
                    else
                        oldText = blob.GetContentText();
                }
            }

            // New side: from the working directory.
            if (change.Kind != ChangeKind.Deleted)
            {
                var full = Path.Combine(repo.Info.WorkingDirectory, change.Path);
                if (File.Exists(full))
                {
                    var bytes = File.ReadAllBytes(full);
                    if (LooksBinary(bytes))
                        isBinary = true;
                    else
                        newText = DecodeText(bytes);
                }
            }

            return isBinary ? new FileContent("", "", true) : new FileContent(oldText, newText, false);
        }
    }

    private static bool LooksBinary(byte[] bytes)
    {
        var probe = Math.Min(bytes.Length, 8000);
        return Array.IndexOf(bytes, (byte)0, 0, probe) >= 0;
    }

    private static string DecodeText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static ChangeKind MapStatus(LibGitChangeKind status) => status switch
    {
        LibGitChangeKind.Added => ChangeKind.Added,
        LibGitChangeKind.Deleted => ChangeKind.Deleted,
        LibGitChangeKind.Renamed => ChangeKind.Renamed,
        LibGitChangeKind.Copied => ChangeKind.Added,
        LibGitChangeKind.Untracked => ChangeKind.Untracked,
        _ => ChangeKind.Modified,
    };

    /// <summary>Counts text lines in a working-dir file; flags it binary on a NUL byte.</summary>
    private static (int lines, bool isBinary) CountWorkdirLines(Repository repo, string relativePath)
    {
        try
        {
            var full = Path.Combine(repo.Info.WorkingDirectory, relativePath);
            var bytes = File.ReadAllBytes(full);
            if (bytes.Length == 0)
                return (0, false);

            var probe = Math.Min(bytes.Length, 8000);
            if (Array.IndexOf(bytes, (byte)0, 0, probe) >= 0)
                return (0, true);

            var lines = 0;
            foreach (var b in bytes)
                if (b == (byte)'\n')
                    lines++;
            if (bytes[^1] != (byte)'\n')
                lines++;

            return (lines, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, false);
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
