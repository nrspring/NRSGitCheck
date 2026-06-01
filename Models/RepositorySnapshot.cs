using System.Collections.Generic;

namespace NRSGitCheck.Models;

/// <summary>A local branch as exposed to the UI (no LibGit2Sharp types leak out).</summary>
public sealed record BranchInfo(string Name, string Sha, string ShortSha, bool IsCurrent);

/// <summary>
/// An immutable, UI-facing snapshot of an opened repository (FR-1, FR-8, FR-11).
/// Built on the background thread; contains only plain values so it is safe to
/// hand to view models.
/// </summary>
public sealed record RepositorySnapshot(
    string WorkingDirectory,
    string Name,
    string CurrentBranch,
    bool IsDetachedHead,
    bool IsHeadUnborn,
    string HeadShortSha,
    IReadOnlyList<BranchInfo> LocalBranches,
    string? DefaultParentBranch);
