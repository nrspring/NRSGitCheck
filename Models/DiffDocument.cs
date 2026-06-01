using System;
using System.Collections.Generic;

namespace NRSGitCheck.Models;

/// <summary>A contiguous block of changes plus surrounding context (FR-18).</summary>
public sealed record DiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    string Header,
    IReadOnlyList<DiffLine> Lines);

/// <summary>
/// The full computed diff for one file. For binary or oversized files no hunks
/// are produced and the corresponding flag is set instead (FR-17, FR-22).
/// </summary>
public sealed class DiffDocument
{
    public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();
    public bool IsBinary { get; init; }
    public bool IsTooLarge { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }

    public bool HasChanges => Hunks.Count > 0;

    public static DiffDocument Binary() => new() { IsBinary = true };
    public static DiffDocument TooLarge() => new() { IsTooLarge = true };
}
