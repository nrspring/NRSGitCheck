using System.Collections;
using System.Collections.Generic;
using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>What a run of changed rows shows in the scrollbar overview strip.</summary>
public enum ChangeMarkerKind
{
    Added,
    Removed,

    /// <summary>A side-by-side row that removes on the left and adds on the right.</summary>
    Mixed,
}

/// <summary>
/// One contiguous run of changed rows, as inclusive indices into the rendered row
/// list. The strip turns these into ticks by scaling against the total row count.
/// </summary>
public readonly record struct ChangeMarker(int Start, int End, ChangeMarkerKind Kind);

/// <summary>The whole file's changes plus the row count they are positioned against.</summary>
public sealed record ChangeOverview(IReadOnlyList<ChangeMarker> Markers, int RowCount);

/// <summary>
/// Reduces rendered diff rows to the marks drawn beside a pane's scrollbar (FR-24
/// navigation, seen at a glance): one tick per contiguous run of same-kind rows,
/// so a 40-line insertion is one bar rather than 40 stacked slivers.
/// </summary>
public static class ChangeMarkers
{
    public static readonly ChangeOverview Empty = new(new List<ChangeMarker>(), 0);

    /// <summary>The mark a single rendered row contributes, or null when it is not a change.</summary>
    public static ChangeMarkerKind? KindOf(object? row) => row switch
    {
        InlineDiffRow inline => FromLine(inline.Kind),
        // Both panes share one row list, so a side row is described by both of its
        // cells; that keeps the left and right strips aligned with each other.
        SideDiffRow side => Combine(FromLine(side.Left.Kind), FromLine(side.Right.Kind)),
        _ => null,
    };

    public static ChangeOverview Build(IEnumerable? rows)
    {
        if (rows is null)
            return Empty;

        var markers = new List<ChangeMarker>();
        var count = 0;

        foreach (var row in rows)
        {
            var index = count++;
            if (KindOf(row) is not { } kind)
                continue;

            // Extend the run in progress when this row abuts it and reads the same.
            if (markers.Count > 0 && markers[^1].End == index - 1 && markers[^1].Kind == kind)
                markers[^1] = markers[^1] with { End = index };
            else
                markers.Add(new ChangeMarker(index, index, kind));
        }

        return new ChangeOverview(markers, count);
    }

    private static ChangeMarkerKind? FromLine(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => ChangeMarkerKind.Added,
        DiffLineKind.Removed => ChangeMarkerKind.Removed,
        _ => null,
    };

    private static ChangeMarkerKind? Combine(ChangeMarkerKind? left, ChangeMarkerKind? right)
    {
        if (left is null)
            return right;
        if (right is null || left == right)
            return left;
        return ChangeMarkerKind.Mixed;
    }
}
