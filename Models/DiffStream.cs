using System;
using System.Collections.Generic;

namespace NRSGitCheck.Models;

/// <summary>
/// A lazily-produced diff. The binary / oversized flags are known immediately,
/// while <see cref="Hunks"/> is enumerated on demand so each hunk can be rendered
/// as it is computed, letting the top of a large file appear before the bottom is
/// finished (FR-18, NFR-1).
/// </summary>
public sealed class DiffStream
{
    public bool IsBinary { get; init; }
    public bool IsTooLarge { get; init; }

    /// <summary>
    /// Hunks in document order. Enumeration drives the (lazy) diff computation, so
    /// pulling the first hunk only computes as far as the first hunk.
    /// </summary>
    public IEnumerable<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();

    public static DiffStream Binary() => new() { IsBinary = true };
    public static DiffStream TooLarge() => new() { IsTooLarge = true };
}
