using System;
using System.Buffers;
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

    /// <summary>Discovered repo path, kept so background work can open its own handle.</summary>
    private string? _repoPath;

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
            _repoPath = discovered;
            return BuildSnapshot(_repo);
        }
    }

    public ResolvedComparison ResolveComparison(
        ComparisonMode mode, string? otherBranch, string? parentBranch, string? commitSha = null)
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

                case ComparisonMode.SinceCommit:
                {
                    if (string.IsNullOrWhiteSpace(commitSha))
                        return ResolvedComparison.Unresolved("Select a commit to compare against.");

                    var commit = repo.Lookup<Commit>(commitSha);
                    if (commit is null)
                        return ResolvedComparison.Unresolved($"Commit '{Shorten(commitSha)}' was not found.");

                    return ResolvedComparison.Resolved(commit.Sha, $"{Shorten(commit.Sha)} ({Summarize(commit)})");
                }

                case ComparisonMode.VsMain:
                {
                    var main = DetectMainBranch(repo);
                    if (main is null)
                        return ResolvedComparison.Unresolved("No main/master branch was found in this repository.");

                    var mainTip = main.Tip;
                    if (mainTip is null)
                        return ResolvedComparison.Unresolved($"Branch '{main.FriendlyName}' has no commits.");

                    var mergeBase = repo.ObjectDatabase.FindMergeBase(repo.Head.Tip, mainTip);
                    if (mergeBase is null)
                        return ResolvedComparison.Unresolved($"No common history with '{main.FriendlyName}'.");

                    return ResolvedComparison.Resolved(
                        mergeBase.Sha, $"{main.FriendlyName} base ({Shorten(mergeBase.Sha)})");
                }

                default:
                    return ResolvedComparison.Unresolved("Unknown comparison mode.");
            }
        }
    }

    public IReadOnlyList<CommitInfo> GetBranchCommits(string? mainBranch, int maxCount = 200)
    {
        lock (_gate)
        {
            var repo = _repo ?? throw new GitException("No repository is open.");

            if (repo.Info.IsHeadUnborn || repo.Head.Tip is null)
                return Array.Empty<CommitInfo>();

            var head = repo.Head.Tip;

            // Where the branch started: the merge-base with main. Null when there is
            // no main, no shared history, or we *are* main (base == HEAD) -- in those
            // cases the walk just yields recent history so the picker is never empty.
            string? branchStartSha = null;
            var main = mainBranch is null ? DetectMainBranch(repo) : FindBranch(repo, mainBranch);
            if (main?.Tip is not null && main.Tip.Sha != head.Sha)
            {
                var mergeBase = repo.ObjectDatabase.FindMergeBase(head, main.Tip);
                if (mergeBase is not null && mergeBase.Sha != head.Sha)
                    branchStartSha = mergeBase.Sha;
            }

            var filter = new CommitFilter
            {
                IncludeReachableFrom = head,
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            };

            var result = new List<CommitInfo>();
            foreach (var commit in repo.Commits.QueryBy(filter))
            {
                var isStart = commit.Sha == branchStartSha;
                result.Add(new CommitInfo(
                    commit.Sha,
                    Shorten(commit.Sha),
                    Summarize(commit),
                    commit.Author?.Name ?? "",
                    commit.Author?.When ?? commit.Committer?.When ?? DateTimeOffset.MinValue,
                    isStart));

                if (isStart || result.Count >= maxCount)
                    break;
            }

            return result;
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

            // Files not under version control, surfaced separately as Untracked (FR-13).
            var untrackedSet = new HashSet<string>(StringComparer.Ordinal);
            var status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
                IncludeIgnored = false,
            });
            foreach (var u in status.Untracked)
                untrackedSet.Add(u.FilePath);

            // Fast, metadata-only comparison: paths + change kind, with NO per-file
            // content diff. Line counts and the binary flag for tracked files are
            // computed later by GetChangeStats so opening stays responsive (NFR-1).
            //
            // Including the index as a diff target is what makes this usable on a big
            // repository. Against the working directory alone libgit2 has to hash every
            // tracked file on every call; with the index in play it consults the index's
            // stat cache and only hashes files whose size/mtime actually moved -- the
            // same trick `git diff HEAD` uses. Measured on a 6,000-file tree: 2,000ms
            // down to 28ms, with an identical set of paths and change kinds.
            var tree = repo.Diff.Compare<TreeChanges>(
                commit.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory);
            foreach (var entry in tree)
            {
                if (untrackedSet.Contains(entry.Path))
                    continue; // handled in the untracked pass below

                result.Add(new FileChange(
                    entry.Path,
                    entry.OldPath != entry.Path ? entry.OldPath : null,
                    MapStatus(entry.Status),
                    LinesAdded: 0,
                    LinesDeleted: 0,
                    IsBinary: false));
                seen.Add(entry.Path);
            }

            // Untracked files: counted cheaply from disk (which also flags binary),
            // in parallel since it is pure I/O. Dedup on this thread, then fan out.
            var workdir = repo.Info.WorkingDirectory;
            var extra = untrackedSet.Where(path => seen.Add(path)).ToList();
            if (extra.Count > 0)
            {
                var counted = extra
                    .AsParallel()
                    .Select(path =>
                    {
                        var (lines, isBinary) = CountWorkdirLines(workdir, path);
                        return new FileChange(path, null, ChangeKind.Untracked, lines, 0, isBinary);
                    })
                    .ToList();
                result.AddRange(counted);
            }

            return result
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyDictionary<string, FileStats> GetChangeStats(string baseCommitSha)
    {
        string? path;
        lock (_gate)
            path = _repoPath;

        if (string.IsNullOrEmpty(path))
            return EmptyStats;

        // Independent repository handle: separate LibGit2Sharp instances don't share
        // state, so this heavy content diff runs on a background thread without
        // contending with interactive reads on the main handle (NFR-1).
        using var repo = new Repository(path);

        var commit = repo.Lookup<Commit>(baseCommitSha);
        if (commit is null)
            return EmptyStats;

        var untracked = new HashSet<string>(StringComparer.Ordinal);
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            IncludeIgnored = false,
        });
        foreach (var u in status.Untracked)
            untracked.Add(u.FilePath);

        var result = new Dictionary<string, FileStats>(StringComparer.Ordinal);

        // Same index-as-target trick as GetChanges: 2,700ms down to 700ms on a
        // 6,000-file tree, with identical line counts and binary flags.
        var patch = repo.Diff.Compare<Patch>(
            commit.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory);
        foreach (var entry in patch)
        {
            if (untracked.Contains(entry.Path))
                continue; // untracked counts already set from disk by GetChanges

            result[entry.Path] = new FileStats(entry.LinesAdded, entry.LinesDeleted, entry.IsBinaryComparison);
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, FileStats> EmptyStats =
        new Dictionary<string, FileStats>();

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

    /// <summary>
    /// Above this size an untracked file is not counted at all. Such a file is far
    /// past what the diff view will render anyway, and counting it means re-reading
    /// it from disk on every refresh -- which auto-refresh does every few seconds.
    /// </summary>
    private const long MaxCountedFileBytes = 4L * 1024 * 1024;

    /// <summary>Counts text lines in a working-dir file; flags it binary on a NUL byte.
    /// Takes the working-directory path (not the repo) so it is safe to call in parallel.
    /// Reads through a small pooled buffer rather than materializing the whole file, so
    /// a stray multi-gigabyte file in the tree cannot blow up memory.</summary>
    private static (int lines, bool isBinary) CountWorkdirLines(string? workingDirectory, string relativePath)
    {
        try
        {
            if (string.IsNullOrEmpty(workingDirectory))
                return (0, false);

            var full = Path.Combine(workingDirectory, relativePath);
            var info = new FileInfo(full);
            if (!info.Exists || info.Length == 0 || info.Length > MaxCountedFileBytes)
                return (0, false);

            using var stream = new FileStream(
                full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 0, useAsync: false);

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var lines = 0;
                var lastByte = 0;
                var any = false;
                int read;

                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (!any)
                    {
                        // Binary probe on the first chunk only, matching Git's heuristic.
                        any = true;
                        if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 8000)) >= 0)
                            return (0, true);
                    }

                    for (var i = 0; i < read; i++)
                        if (buffer[i] == (byte)'\n')
                            lines++;

                    lastByte = buffer[read - 1];
                }

                if (any && lastByte != (byte)'\n')
                    lines++;

                return (lines, false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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
        var main = DetectMainBranch(repo)?.FriendlyName;
        var origin = repo.Network.Remotes["origin"] ?? repo.Network.Remotes.FirstOrDefault();
        var hasRemote = origin is not null;

        return new RepositorySnapshot(
            workdir, name, currentBranch, isDetached, isUnborn, headShort, locals, parent,
            main, hasRemote, origin?.Url);
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

    /// <summary>Finds a branch by friendly name, preferring a local one over a remote-tracking one.</summary>
    private static Branch? FindBranch(Repository repo, string name) =>
        FindLocalBranch(repo, name) ?? repo.Branches.FirstOrDefault(b => b.FriendlyName == name);

    /// <summary>
    /// The repository's integration branch: a local <c>main</c>/<c>master</c> if there
    /// is one, otherwise the remote-tracking equivalent so the comparison still works
    /// in a repo that has never checked main out locally. Returns null if neither exists.
    /// </summary>
    private static Branch? DetectMainBranch(Repository repo)
    {
        foreach (var name in new[] { "main", "master" })
            if (FindLocalBranch(repo, name) is { } local)
                return local;

        foreach (var name in new[] { "origin/main", "origin/master" })
            if (repo.Branches.FirstOrDefault(b => b.IsRemote && b.FriendlyName == name) is { } remote)
                return remote;

        return null;
    }

    /// <summary>First line of a commit message, clipped so it fits the picker.</summary>
    private static string Summarize(Commit commit)
    {
        var text = commit.MessageShort;
        if (string.IsNullOrWhiteSpace(text))
            text = (commit.Message ?? "").Split('\n')[0];

        text = text.Trim();
        return text.Length <= 72 ? text : text[..69] + "...";
    }

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
