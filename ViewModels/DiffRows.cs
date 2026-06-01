using System;
using System.Collections.Generic;
using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// A run of text within a rendered diff line. <see cref="Highlight"/> drives the
/// word-level emphasis (FR-23): Unchanged = none, Added/Removed = highlighted.
/// </summary>
public sealed class RenderSegment
{
    public RenderSegment(string text, WordSegmentKind highlight)
    {
        Text = text;
        Highlight = highlight;
    }

    public string Text { get; }
    public WordSegmentKind Highlight { get; }
}

/// <summary>Separator row carrying a hunk header (e.g. <c>@@ -1,4 +1,6 @@</c>).</summary>
public sealed class HunkSeparatorRow
{
    public string Header { get; init; } = string.Empty;
}

/// <summary>A single row in the inline/unified diff list.</summary>
public sealed class InlineDiffRow
{
    public string OldNumber { get; init; } = string.Empty;
    public string NewNumber { get; init; } = string.Empty;
    public string Marker { get; init; } = " ";
    public DiffLineKind Kind { get; init; }
    public IReadOnlyList<RenderSegment> Segments { get; init; } = Array.Empty<RenderSegment>();
}

/// <summary>One side (old or new) of a side-by-side row; may be an empty filler.</summary>
public sealed class SideCell
{
    public bool IsEmpty { get; init; }
    public string Number { get; init; } = string.Empty;
    public DiffLineKind Kind { get; init; }
    public IReadOnlyList<RenderSegment> Segments { get; init; } = Array.Empty<RenderSegment>();

    public static SideCell Empty { get; } = new() { IsEmpty = true };
}

/// <summary>A row in the side-by-side diff list, holding both sides for alignment.</summary>
public sealed class SideDiffRow
{
    public SideCell Left { get; init; } = SideCell.Empty;
    public SideCell Right { get; init; } = SideCell.Empty;
}
