using System.Collections.Generic;

namespace NRSGitCheck.Models;

/// <summary>Role of a line in a unified diff (FR-18).</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

/// <summary>Role of an intra-line segment for word-level highlighting (FR-23).</summary>
public enum WordSegmentKind
{
    Unchanged,
    Added,
    Removed,
}

/// <summary>A run of text within a modified line, tagged for word-level highlighting.</summary>
public sealed record WordSegment(string Text, WordSegmentKind Kind);

/// <summary>
/// A single line of a diff. Context lines carry both old and new line numbers;
/// removed lines carry only the old number, added lines only the new number.
/// When the line is part of a modified pair, <see cref="Segments"/> holds the
/// word-level breakdown (FR-23).
/// </summary>
public sealed class DiffLine
{
    public DiffLine(DiffLineKind kind, int? oldLineNumber, int? newLineNumber, string text)
    {
        Kind = kind;
        OldLineNumber = oldLineNumber;
        NewLineNumber = newLineNumber;
        Text = text;
    }

    public DiffLineKind Kind { get; }
    public int? OldLineNumber { get; }
    public int? NewLineNumber { get; }
    public string Text { get; }

    /// <summary>Word-level segments when this line belongs to a modified pair; otherwise null.</summary>
    public IReadOnlyList<WordSegment>? Segments { get; set; }
}
