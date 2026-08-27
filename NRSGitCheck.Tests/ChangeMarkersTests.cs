using System.Collections.Generic;
using NRSGitCheck.Models;
using NRSGitCheck.ViewModels;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// The scrollbar overview strip is driven entirely by these marks, so the row-to-mark
/// reduction is checked here rather than through a rendered control.
/// </summary>
public sealed class ChangeMarkersTests
{
    private static InlineDiffRow Inline(DiffLineKind kind) => new() { Kind = kind };

    private static SideDiffRow Side(DiffLineKind? left, DiffLineKind? right) => new()
    {
        Left = left is { } l ? new SideCell { Kind = l } : SideCell.Empty,
        Right = right is { } r ? new SideCell { Kind = r } : SideCell.Empty,
    };

    [Fact]
    public void No_rows_produce_no_markers()
    {
        var overview = ChangeMarkers.Build(new List<object>());

        Assert.Empty(overview.Markers);
        Assert.Equal(0, overview.RowCount);
    }

    [Fact]
    public void Null_rows_are_treated_as_empty() => Assert.Empty(ChangeMarkers.Build(null).Markers);

    [Fact]
    public void Context_and_hunk_headers_are_not_marked()
    {
        var rows = new List<object>
        {
            new HunkSeparatorRow(),
            Inline(DiffLineKind.Context),
            Inline(DiffLineKind.Context),
        };

        var overview = ChangeMarkers.Build(rows);

        Assert.Empty(overview.Markers);
        Assert.Equal(3, overview.RowCount);
    }

    [Fact]
    public void Consecutive_rows_of_one_kind_collapse_into_a_single_marker()
    {
        var rows = new List<object>
        {
            new HunkSeparatorRow(),
            Inline(DiffLineKind.Context),
            Inline(DiffLineKind.Added),
            Inline(DiffLineKind.Added),
            Inline(DiffLineKind.Added),
            Inline(DiffLineKind.Context),
        };

        var marker = Assert.Single(ChangeMarkers.Build(rows).Markers);

        Assert.Equal(new ChangeMarker(2, 4, ChangeMarkerKind.Added), marker);
    }

    [Fact]
    public void Adjacent_removed_and_added_runs_stay_separate_marks()
    {
        var rows = new List<object>
        {
            Inline(DiffLineKind.Removed),
            Inline(DiffLineKind.Added),
        };

        var markers = ChangeMarkers.Build(rows).Markers;

        Assert.Equal(2, markers.Count);
        Assert.Equal(new ChangeMarker(0, 0, ChangeMarkerKind.Removed), markers[0]);
        Assert.Equal(new ChangeMarker(1, 1, ChangeMarkerKind.Added), markers[1]);
    }

    [Fact]
    public void Runs_split_by_context_do_not_merge()
    {
        var rows = new List<object>
        {
            Inline(DiffLineKind.Added),
            Inline(DiffLineKind.Context),
            Inline(DiffLineKind.Added),
        };

        var markers = ChangeMarkers.Build(rows).Markers;

        Assert.Equal(2, markers.Count);
        Assert.Equal(new ChangeMarker(0, 0, ChangeMarkerKind.Added), markers[0]);
        Assert.Equal(new ChangeMarker(2, 2, ChangeMarkerKind.Added), markers[1]);
    }

    [Fact]
    public void Side_row_with_both_sides_changed_reads_as_mixed()
    {
        var rows = new List<object> { Side(DiffLineKind.Removed, DiffLineKind.Added) };

        Assert.Equal(ChangeMarkerKind.Mixed, Assert.Single(ChangeMarkers.Build(rows).Markers).Kind);
    }

    [Fact]
    public void Side_row_with_a_filler_cell_takes_the_other_sides_kind()
    {
        var rows = new List<object>
        {
            Side(null, DiffLineKind.Added),
            Side(DiffLineKind.Removed, null),
        };

        var markers = ChangeMarkers.Build(rows).Markers;

        Assert.Equal(2, markers.Count);
        Assert.Equal(ChangeMarkerKind.Added, markers[0].Kind);
        Assert.Equal(ChangeMarkerKind.Removed, markers[1].Kind);
    }

    [Fact]
    public void Side_context_rows_are_not_marked() =>
        Assert.Empty(ChangeMarkers.Build(new List<object> { Side(DiffLineKind.Context, DiffLineKind.Context) }).Markers);

    [Fact]
    public void Row_count_covers_every_row_so_positions_scale_over_the_whole_file()
    {
        var rows = new List<object>
        {
            new HunkSeparatorRow(),
            Inline(DiffLineKind.Context),
            Inline(DiffLineKind.Removed),
        };

        Assert.Equal(3, ChangeMarkers.Build(rows).RowCount);
    }
}
