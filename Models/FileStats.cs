namespace NRSGitCheck.Models;

/// <summary>
/// Per-file line counts and binary flag, computed off the critical path after the
/// changed-files list is shown (FR-14, NFR-1). Keyed by file path by the caller.
/// </summary>
public readonly record struct FileStats(int LinesAdded, int LinesDeleted, bool IsBinary);
