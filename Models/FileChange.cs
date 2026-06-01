namespace NRSGitCheck.Models;

/// <summary>
/// A single changed file between the comparison base and the working tree (FR-12..17).
/// Line counts are zero for binary files, which are flagged via <see cref="IsBinary"/>.
/// </summary>
public sealed record FileChange(
    string Path,
    string? OldPath,
    ChangeKind Kind,
    int LinesAdded,
    int LinesDeleted,
    bool IsBinary);
