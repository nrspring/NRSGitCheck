using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using NRSGitCheck.ViewModels;
using Xunit;
using ChangeKind = NRSGitCheck.Models.ChangeKind;

namespace NRSGitCheck.Tests;

/// <summary>
/// Phase 5 exit check at the row-model level: both layouts build correct rows
/// with decorations and word-level segments.
/// </summary>
public sealed class DiffViewModelTests
{
    private sealed class StubClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task<bool> SetTextAsync(string? text)
        {
            Text = text;
            return Task.FromResult(true);
        }
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public int SaveCount { get; private set; }
        public void Load() { }
        public void Save() => SaveCount++;
        public void AddRecentRepository(string repositoryPath) { }
        public void RemoveRecentRepository(string repositoryPath) { }
        public bool AddTrackedRepository(string repositoryPath) => false;
        public void RemoveTrackedRepository(string repositoryPath) { }
    }

    private sealed class StubDiff : IDiffService
    {
        private readonly DiffDocument _doc;
        public StubDiff(DiffDocument doc) => _doc = doc;

        public DiffStream BuildDiffStream(string baseCommitSha, FileChange change, int contextLines = 3, bool wholeFile = false) =>
            new() { IsBinary = _doc.IsBinary, IsTooLarge = _doc.IsTooLarge, Hunks = _doc.Hunks };
    }

    private static FileChange Change() => new("file.txt", null, ChangeKind.Modified, 0, 0, false);

    [Fact]
    public async Task Modified_file_builds_both_layouts_with_word_segments()
    {
        var doc = DiffEngine.Compute("the quick brown fox\n", "the slow brown fox\n");
        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings(), new StubClipboard());

        await vm.LoadAsync("base", Change());

        Assert.True(vm.ShowDiff);
        Assert.Contains(vm.InlineRows, r => r is HunkSeparatorRow);

        var inline = vm.InlineRows.OfType<InlineDiffRow>().ToList();
        Assert.Contains(inline, r => r.Segments.Any(s => s.Highlight == WordSegmentKind.Removed));
        Assert.Contains(inline, r => r.Segments.Any(s => s.Highlight == WordSegmentKind.Added));

        // Side-by-side pairs the modified line: both sides present and tinted.
        var side = vm.SideRows.OfType<SideDiffRow>().ToList();
        Assert.Contains(side, r =>
            !r.Left.IsEmpty && !r.Right.IsEmpty &&
            r.Left.Kind == DiffLineKind.Removed && r.Right.Kind == DiffLineKind.Added);
    }

    [Fact]
    public async Task Added_file_side_rows_have_empty_left_side()
    {
        var doc = DiffEngine.Compute("", "new1\nnew2\n");
        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings(), new StubClipboard());

        await vm.LoadAsync("base", Change());

        var side = vm.SideRows.OfType<SideDiffRow>().ToList();
        Assert.NotEmpty(side);
        Assert.All(side, r => Assert.True(r.Left.IsEmpty));
        Assert.All(side, r => Assert.False(r.Right.IsEmpty));
    }

    [Fact]
    public async Task Binary_document_shows_message_not_diff()
    {
        var vm = new DiffViewModel(new StubDiff(DiffDocument.Binary()), new StubSettings(), new StubClipboard());

        await vm.LoadAsync("base", Change());

        Assert.True(vm.IsBinary);
        Assert.True(vm.ShowMessage);
        Assert.False(vm.ShowDiff);
        Assert.Empty(vm.InlineRows);
    }

    [Fact]
    public async Task Hunk_navigation_moves_through_then_stops()
    {
        var oldText = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line{i}")) + "\n";
        var newText = string.Join("\n", Enumerable.Range(1, 40).Select(i =>
            i == 5 ? "line5-x" : i == 35 ? "line35-x" : $"line{i}")) + "\n";
        var doc = DiffEngine.Compute(oldText, newText, contextLines: 3);
        Assert.Equal(2, doc.Hunks.Count);

        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings(), new StubClipboard());
        var scrolls = 0;
        vm.ScrollToRequested += _ => scrolls++;

        await vm.LoadAsync("base", Change()); // lands on the first section

        Assert.True(vm.GoToNextSection());        // -> second section
        Assert.False(vm.GoToNextSection());       // already at last
        Assert.True(vm.GoToPreviousSection());    // -> first section
        Assert.False(vm.GoToPreviousSection());   // already at first
        Assert.True(scrolls >= 2);
    }

    [Fact]
    public async Task Sections_are_counted_per_changed_area_not_per_hunk()
    {
        // Two edits four lines apart: close enough that a context of 3 merges them
        // into ONE hunk, but they are still two separate changed areas on screen.
        var oldText = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line{i}")) + "\n";
        var newText = string.Join("\n", Enumerable.Range(1, 40).Select(i =>
            i == 10 ? "line10-x" : i == 14 ? "line14-x" : $"line{i}")) + "\n";

        var doc = DiffEngine.Compute(oldText, newText, contextLines: 3);
        Assert.Single(doc.Hunks);                 // one hunk...

        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings(), new StubClipboard());
        await vm.LoadAsync("base", Change());

        Assert.Equal(2, vm.SectionCount);         // ...but two navigable sections
        Assert.Equal(0, vm.CurrentSectionIndex);
        Assert.True(vm.GoToNextSection());
        Assert.Equal(1, vm.CurrentSectionIndex);
        Assert.False(vm.GoToNextSection());
    }

    [Fact]
    public async Task Whole_file_mode_still_exposes_every_changed_area()
    {
        // Whole-file mode renders the file as a single hunk; navigation must still
        // stop at each edit rather than treating the file as one big change.
        var oldText = string.Join("\n", Enumerable.Range(1, 90).Select(i => $"line{i}")) + "\n";
        var newText = string.Join("\n", Enumerable.Range(1, 90).Select(i =>
            i is 10 or 40 or 70 ? $"line{i}-x" : $"line{i}")) + "\n";

        var doc = DiffEngine.Compute(oldText, newText, contextLines: 3, wholeFile: true);
        Assert.Single(doc.Hunks);

        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings(), new StubClipboard());
        await vm.LoadAsync("base", Change());

        Assert.Equal(3, vm.SectionCount);
        Assert.True(vm.GoToNextSection());
        Assert.True(vm.GoToNextSection());
        Assert.False(vm.GoToNextSection());       // three areas, then done
    }

    [Fact]
    public async Task Both_layouts_agree_on_the_section_count()
    {
        var oldText = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"line{i}")) + "\n";
        var newText = string.Join("\n", Enumerable.Range(1, 60).Select(i =>
            i is 5 or 9 or 30 or 50 ? $"line{i}-x" : $"line{i}")) + "\n";

        var doc = DiffEngine.Compute(oldText, newText, contextLines: 3);
        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings(), new StubClipboard());
        await vm.LoadAsync("base", Change());

        vm.Layout = DiffLayout.Inline;
        var inline = vm.SectionCount;
        vm.Layout = DiffLayout.SideBySide;

        Assert.Equal(4, inline);
        Assert.Equal(inline, vm.SectionCount);    // index stays valid across a layout switch
    }

    [Fact]
    public void ToggleLayout_flips_and_persists()
    {
        var settings = new StubSettings();
        settings.Settings.LastDiffLayout = DiffLayout.SideBySide;
        var vm = new DiffViewModel(new StubDiff(DiffDocument.Binary()), settings, new StubClipboard());

        Assert.True(vm.IsSideBySide);

        vm.ToggleLayoutCommand.Execute(null);

        Assert.True(vm.IsInline);
        Assert.Equal(DiffLayout.Inline, settings.Settings.LastDiffLayout);
        Assert.True(settings.SaveCount > 0);
    }

    [Theory]
    [InlineData(10, 100, 13)]   // room below: the change gets its trailing context
    [InlineData(98, 100, 99)]   // near the end: scroll as far as the file allows
    [InlineData(99, 100, 99)]   // last row: nothing left to reveal
    [InlineData(0, 1, 0)]       // single row
    public void Scrolling_to_a_change_reveals_rows_past_it(int index, int rowCount, int expected)
    {
        Assert.Equal(expected, DiffViewModel.TrailingContextRow(index, rowCount));
    }

    [Fact]
    public void Trailing_context_has_nothing_to_reveal_in_an_empty_diff()
    {
        Assert.Equal(-1, DiffViewModel.TrailingContextRow(0, 0));
    }

    [Fact]
    public async Task A_multi_line_change_is_anchored_over_its_whole_extent()
    {
        // Three consecutive edited lines are one section: scrolling to it has to
        // account for its last line, not just the first.
        var vm = new DiffViewModel(
            new StubDiff(DiffEngine.Compute(
                Lines("a", "b", "c", "d", "e", "f", "g"),
                Lines("a", "B", "C", "D", "e", "f", "g"))),
            new StubSettings(), new StubClipboard());
        vm.Layout = DiffLayout.Inline;

        SectionAnchor? section = null;
        vm.ScrollToRequested += a => section = a;
        await vm.LoadAsync("base", Change());

        Assert.NotNull(section);

        var rows = vm.InlineRows.ToList();
        var start = rows.IndexOf(section!.Start);
        var end = rows.IndexOf(section.End);

        Assert.True(start >= 0 && end > start);              // the section spans rows
        Assert.All(rows.GetRange(start, end - start + 1),
            r => Assert.NotEqual(DiffLineKind.Context, ((InlineDiffRow)r).Kind));
        Assert.Equal(DiffLineKind.Context, ((InlineDiffRow)rows[end + 1]).Kind);   // ends where context resumes
    }

    [Fact]
    public async Task A_single_line_change_anchors_start_and_end_to_the_same_row()
    {
        var vm = new DiffViewModel(
            new StubDiff(DiffEngine.Compute(Lines("a", "b", "c"), Lines("a", "B", "c"))),
            new StubSettings(), new StubClipboard());

        SectionAnchor? section = null;
        vm.ScrollToRequested += a => section = a;
        await vm.LoadAsync("base", Change());

        Assert.NotNull(section);
        Assert.Same(section!.Start, section.End);
    }

    [Fact]
    public async Task Side_by_side_anchors_span_the_section_too()
    {
        var vm = new DiffViewModel(
            new StubDiff(DiffEngine.Compute(
                Lines("a", "b", "c", "d", "e"),
                Lines("a", "B", "C", "d", "e"))),
            new StubSettings(), new StubClipboard());
        vm.Layout = DiffLayout.SideBySide;

        SectionAnchor? section = null;
        vm.ScrollToRequested += a => section = a;
        await vm.LoadAsync("base", Change());

        Assert.NotNull(section);

        var rows = vm.SideRows.ToList();
        Assert.True(rows.IndexOf(section!.End) > rows.IndexOf(section.Start));
    }

    [Fact]
    public async Task Copying_selected_inline_rows_yields_plain_code()
    {
        var clipboard = new StubClipboard();
        var vm = new DiffViewModel(
            new StubDiff(DiffEngine.Compute(Lines("a", "b", "c"), Lines("a", "B", "c"))),
            new StubSettings(), clipboard);
        vm.Layout = DiffLayout.Inline;
        await vm.LoadAsync("base", Change());

        var rows = vm.InlineRows.OfType<InlineDiffRow>().ToList();
        var copied = await vm.CopySelectionAsync(rows.Cast<object>(), DiffPane.Left);

        Assert.Equal(rows.Count, copied);
        Assert.NotNull(clipboard.Text);
        Assert.DoesNotContain("@@", clipboard.Text);            // no hunk headers
        Assert.DoesNotContain("+", clipboard.Text);             // no +/- markers
        Assert.Contains("B", clipboard.Text);
    }

    [Fact]
    public void Copy_text_drops_hunk_headers_and_keeps_line_order()
    {
        var rows = new object[]
        {
            new HunkSeparatorRow { Header = "@@ -1,3 +1,3 @@" },
            Row("first"),
            Row("second"),
        };

        var text = DiffViewModel.BuildCopyText(rows, DiffPane.Left);

        Assert.Equal("first" + Environment.NewLine + "second", text);
    }

    [Fact]
    public void Copying_a_side_by_side_row_takes_only_the_pane_it_came_from()
    {
        var rows = new object[]
        {
            new SideDiffRow { Left = Cell("old line"), Right = Cell("new line") },
        };

        Assert.Equal("old line", DiffViewModel.BuildCopyText(rows, DiffPane.Left));
        Assert.Equal("new line", DiffViewModel.BuildCopyText(rows, DiffPane.Right));
    }

    [Fact]
    public void A_filler_cell_contributes_no_line_at_all()
    {
        // An added line has nothing on the old side: copying the left pane must not
        // leave a blank line where the filler was.
        var rows = new object[]
        {
            new SideDiffRow { Left = SideCell.Empty, Right = Cell("added") },
            new SideDiffRow { Left = Cell("kept"), Right = Cell("kept") },
        };

        Assert.Equal("kept", DiffViewModel.BuildCopyText(rows, DiffPane.Left));
    }

    [Fact]
    public void Copying_nothing_yields_nothing()
    {
        Assert.Equal(string.Empty, DiffViewModel.BuildCopyText(null, DiffPane.Left));
        Assert.Equal(string.Empty, DiffViewModel.BuildCopyText(Array.Empty<object>(), DiffPane.Left));
        Assert.Equal(string.Empty, DiffViewModel.BuildCopyText(
            new object[] { new HunkSeparatorRow { Header = "@@" } }, DiffPane.Left));
    }

    [Fact]
    public async Task An_empty_selection_copies_nothing_and_reports_zero()
    {
        var clipboard = new StubClipboard();
        var vm = new DiffViewModel(new StubDiff(DiffDocument.Binary()), new StubSettings(), clipboard);

        Assert.Equal(0, await vm.CopySelectionAsync(Array.Empty<object>(), DiffPane.Left));
        Assert.Null(clipboard.Text);
    }

    private static InlineDiffRow Row(string text) => new()
    {
        Kind = DiffLineKind.Context,
        Segments = new[] { new RenderSegment(text, WordSegmentKind.Unchanged, null) },
    };

    private static SideCell Cell(string text) => new()
    {
        Kind = DiffLineKind.Context,
        Segments = new[] { new RenderSegment(text, WordSegmentKind.Unchanged, null) },
    };

    private static string Lines(params string[] lines) =>
        string.Join((char)10, lines) + (char)10;
}
