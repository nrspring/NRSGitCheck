using System.Linq;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Phase 4 exit check: the pure diff engine across add/remove/modify, word-level
/// highlighting, context/hunking, and CRLF handling.
/// </summary>
public sealed class DiffEngineTests
{
    private static DiffLine[] AllLines(DiffDocument doc) =>
        doc.Hunks.SelectMany(h => h.Lines).ToArray();

    [Fact]
    public void Identical_content_has_no_hunks()
    {
        var doc = DiffEngine.Compute("a\nb\nc\n", "a\nb\nc\n");

        Assert.False(doc.HasChanges);
        Assert.Equal(0, doc.LinesAdded);
        Assert.Equal(0, doc.LinesRemoved);
    }

    [Fact]
    public void Crlf_vs_lf_same_content_is_not_a_change()
    {
        var doc = DiffEngine.Compute("a\r\nb\r\nc\r\n", "a\nb\nc\n");

        Assert.False(doc.HasChanges);
    }

    [Fact]
    public void Added_file_is_all_additions()
    {
        var doc = DiffEngine.Compute("", "one\ntwo\nthree\n");

        Assert.Equal(3, doc.LinesAdded);
        Assert.Equal(0, doc.LinesRemoved);
        Assert.All(AllLines(doc), l => Assert.Equal(DiffLineKind.Added, l.Kind));
    }

    [Fact]
    public void Deleted_file_is_all_removals()
    {
        var doc = DiffEngine.Compute("one\ntwo\n", "");

        Assert.Equal(0, doc.LinesAdded);
        Assert.Equal(2, doc.LinesRemoved);
        Assert.All(AllLines(doc), l => Assert.Equal(DiffLineKind.Removed, l.Kind));
    }

    [Fact]
    public void Pure_insertion_keeps_surrounding_context()
    {
        var doc = DiffEngine.Compute("a\nb\nc\n", "a\nb\nNEW\nc\n");

        Assert.Equal(1, doc.LinesAdded);
        Assert.Equal(0, doc.LinesRemoved);

        var added = AllLines(doc).Single(l => l.Kind == DiffLineKind.Added);
        Assert.Equal("NEW", added.Text);
        Assert.Equal(3, added.NewLineNumber);
    }

    [Fact]
    public void Modified_line_produces_paired_word_segments()
    {
        var doc = DiffEngine.Compute("the quick brown fox\n", "the slow brown fox\n");

        var removed = AllLines(doc).Single(l => l.Kind == DiffLineKind.Removed);
        var added = AllLines(doc).Single(l => l.Kind == DiffLineKind.Added);

        Assert.NotNull(removed.Segments);
        Assert.NotNull(added.Segments);

        // The changed word is highlighted; the shared words are unchanged.
        Assert.Contains(removed.Segments!, s => s.Kind == WordSegmentKind.Removed && s.Text == "quick");
        Assert.Contains(added.Segments!, s => s.Kind == WordSegmentKind.Added && s.Text == "slow");
        Assert.Contains(removed.Segments!, s => s.Kind == WordSegmentKind.Unchanged && s.Text.Contains("the"));

        // Reassembling each side's segments reproduces the original line text.
        Assert.Equal("the quick brown fox", string.Concat(removed.Segments!.Select(s => s.Text)));
        Assert.Equal("the slow brown fox", string.Concat(added.Segments!.Select(s => s.Text)));
    }

    [Fact]
    public void Large_unchanged_region_collapses_into_one_hunk_with_context()
    {
        // 40 identical lines, a single change in the middle.
        var oldLines = Enumerable.Range(1, 40).Select(i => $"line{i}");
        var newLines = Enumerable.Range(1, 40).Select(i => i == 20 ? "line20-changed" : $"line{i}");
        var oldText = string.Join("\n", oldLines) + "\n";
        var newText = string.Join("\n", newLines) + "\n";

        var doc = DiffEngine.Compute(oldText, newText, contextLines: 3);

        Assert.Single(doc.Hunks);
        // 3 context above + 1 removed + 1 added + 3 context below = 8 lines, far fewer than 40.
        Assert.True(AllLines(doc).Length <= 8, $"expected a small hunk, got {AllLines(doc).Length} lines");
    }

    [Fact]
    public void Whole_file_mode_emits_one_hunk_covering_every_line()
    {
        // Two changes far apart that would normally split into separate hunks.
        var oldLines = Enumerable.Range(1, 40).Select(i => $"line{i}");
        var newLines = Enumerable.Range(1, 40).Select(i =>
            i == 5 ? "line5-changed" : i == 35 ? "line35-changed" : $"line{i}");

        var doc = DiffEngine.Compute(
            string.Join("\n", oldLines) + "\n",
            string.Join("\n", newLines) + "\n",
            contextLines: 3,
            wholeFile: true);

        Assert.Single(doc.Hunks);
        // The single hunk spans the full file: 40 context/changed + 2 added rows.
        Assert.Equal(42, AllLines(doc).Length);
        Assert.Equal(2, doc.LinesAdded);
        Assert.Equal(2, doc.LinesRemoved);
    }

    [Fact]
    public void Whole_file_mode_with_no_changes_has_no_hunks()
    {
        var doc = DiffEngine.Compute("a\nb\nc\n", "a\nb\nc\n", wholeFile: true);

        Assert.False(doc.HasChanges);
    }

    [Fact]
    public void Separate_changes_far_apart_make_separate_hunks()
    {
        var oldLines = Enumerable.Range(1, 40).Select(i => $"line{i}");
        var newLines = Enumerable.Range(1, 40).Select(i =>
            i == 5 ? "line5-changed" : i == 35 ? "line35-changed" : $"line{i}");

        var doc = DiffEngine.Compute(
            string.Join("\n", oldLines) + "\n",
            string.Join("\n", newLines) + "\n",
            contextLines: 3);

        Assert.Equal(2, doc.Hunks.Count);
    }

    [Fact]
    public void Context_lines_carry_both_line_numbers()
    {
        var doc = DiffEngine.Compute("a\nb\nc\n", "a\nB\nc\n");

        var context = AllLines(doc).First(l => l.Kind == DiffLineKind.Context);
        Assert.NotNull(context.OldLineNumber);
        Assert.NotNull(context.NewLineNumber);
    }
}
