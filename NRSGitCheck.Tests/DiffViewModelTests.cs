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
        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings());

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
        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings());

        await vm.LoadAsync("base", Change());

        var side = vm.SideRows.OfType<SideDiffRow>().ToList();
        Assert.NotEmpty(side);
        Assert.All(side, r => Assert.True(r.Left.IsEmpty));
        Assert.All(side, r => Assert.False(r.Right.IsEmpty));
    }

    [Fact]
    public async Task Binary_document_shows_message_not_diff()
    {
        var vm = new DiffViewModel(new StubDiff(DiffDocument.Binary()), new StubSettings());

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

        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings());
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

        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings());
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

        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings());
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
        var vm = new DiffViewModel(new StubDiff(doc), new StubSettings());
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
        var vm = new DiffViewModel(new StubDiff(DiffDocument.Binary()), settings);

        Assert.True(vm.IsSideBySide);

        vm.ToggleLayoutCommand.Execute(null);

        Assert.True(vm.IsInline);
        Assert.Equal(DiffLayout.Inline, settings.Settings.LastDiffLayout);
        Assert.True(settings.SaveCount > 0);
    }

    [Theory]
    [InlineData(10, 100, 16)]   // room below: the change gets its trailing context
    [InlineData(97, 100, 99)]   // near the end: scroll as far as the file allows
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
}
