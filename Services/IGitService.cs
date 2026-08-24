using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Read-only access to a Git repository (FR-1..2, FR-7..11). The service keeps a
/// single repository open; calls are serialized internally. It never writes to
/// the repository.
/// </summary>
public interface IGitService
{
    /// <summary>
    /// Opens and validates the repository at (or containing) <paramref name="path"/>,
    /// replacing any previously open one. Throws <see cref="GitException"/> with a
    /// user-facing message if the folder is not a valid Git working directory.
    /// </summary>
    RepositorySnapshot OpenRepository(string path);

    /// <summary>
    /// Resolves the chosen comparison mode to a concrete base commit against the
    /// currently open repository (FR-7..9). Does not throw for ordinary "can't
    /// resolve" cases — those come back as an unresolved <see cref="ResolvedComparison"/>.
    /// </summary>
    ResolvedComparison ResolveComparison(
        ComparisonMode mode, string? otherBranch, string? parentBranch, string? commitSha = null);

    /// <summary>
    /// Lists the commits on the current branch, newest first, walking back to (and
    /// including) the point where it diverged from <paramref name="mainBranch"/>.
    /// When there is no main branch, no common history, or the current branch *is*
    /// main, falls back to the most recent <paramref name="maxCount"/> commits so the
    /// picker is never empty.
    /// </summary>
    System.Collections.Generic.IReadOnlyList<CommitInfo> GetBranchCommits(
        string? mainBranch, int maxCount = 200);

    /// <summary>
    /// Lists files that differ between the given base commit and the current working
    /// tree, including untracked files (FR-12..17). This is a fast, metadata-only
    /// pass: tracked files carry zero line counts (filled in later by
    /// <see cref="GetChangeStats"/>); untracked files are counted from disk and have
    /// their binary flag set.
    /// </summary>
    System.Collections.Generic.IReadOnlyList<FileChange> GetChanges(string baseCommitSha);

    /// <summary>
    /// Computes per-file line counts and binary flags for tracked changes, keyed by
    /// path. Runs against an independent repository handle so it can execute on a
    /// background thread concurrently with interactive reads (NFR-1). Untracked files
    /// are omitted (already counted by <see cref="GetChanges"/>).
    /// </summary>
    System.Collections.Generic.IReadOnlyDictionary<string, FileStats> GetChangeStats(string baseCommitSha);

    /// <summary>
    /// Counts uncommitted work in the current working tree: staged and unstaged
    /// changes plus untracked files. Checking a pull request out moves the working
    /// tree, so the toolbar uses this to keep that action out of reach while there
    /// is local work that a checkout could disturb.
    /// </summary>
    int GetUncommittedChangeCount();

    /// <summary>
    /// Retrieves the base (commit) and new (working-tree) text for a changed file,
    /// honoring renames (old content read from the old path). Flags binary content.
    /// </summary>
    FileContent GetFileContent(string baseCommitSha, FileChange change);
}
