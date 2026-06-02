using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NRSGitCheck.Models;
using NRSGitCheck.Services;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// Builds and exposes the rendered diff for the selected file in both inline and
/// side-by-side layouts (FR-18..22). Layout choice is persisted (FR-19).
/// </summary>
public partial class DiffViewModel : ViewModelBase
{
    private readonly IDiffService _diff;
    private readonly ISettingsService _settings;

    public DiffViewModel(IDiffService diff, ISettingsService settings)
    {
        _diff = diff;
        _settings = settings;
        _layout = settings.Settings.LastDiffLayout;
        _showWholeFile = settings.Settings.ShowWholeFileDiff;
    }

    public ObservableCollection<object> InlineRows { get; } = new();
    public ObservableCollection<object> SideRows { get; } = new();

    // Hunk anchors (the separator rows) per layout, for keyboard navigation (FR-24, FR-27).
    private readonly List<object> _inlineAnchors = new();
    private readonly List<object> _sideAnchors = new();
    private int _currentHunkIndex = -1;

    private List<object> ActiveAnchors => IsInline ? _inlineAnchors : _sideAnchors;

    /// <summary>Raised to ask the view to scroll a row into view.</summary>
    public event Action<object>? ScrollToRequested;

    [ObservableProperty]
    private DiffLayout _layout;

    /// <summary>When true, the entire file is shown on both sides rather than just
    /// the changed hunks, with diff highlighting intact. Persisted across launches.</summary>
    [ObservableProperty]
    private bool _showWholeFile;

    /// <summary>Label for the whole-file toggle, describing the action it performs.</summary>
    public string WholeFileToggleLabel => ShowWholeFile ? "Show partial" : "Show full file";

    [ObservableProperty]
    private string? _fileName;

    /// <summary>Full repo-relative path of the selected file, shown in the diff header.</summary>
    [ObservableProperty]
    private string? _filePath;

    [ObservableProperty]
    private string _message = "Select a file to view its changes.";

    [ObservableProperty]
    private bool _isBinary;

    [ObservableProperty]
    private bool _isTooLarge;

    [ObservableProperty]
    private bool _hasChanges;

    [ObservableProperty]
    private bool _hasContent;

    public bool IsSideBySide => Layout == DiffLayout.SideBySide;
    public bool IsInline => Layout == DiffLayout.Inline;

    /// <summary>True when there is a rendered diff to show (not binary/large/empty).</summary>
    public bool ShowDiff => HasContent && HasChanges && !IsBinary && !IsTooLarge;
    public bool ShowMessage => !ShowDiff;

    partial void OnLayoutChanged(DiffLayout value)
    {
        OnPropertyChanged(nameof(IsSideBySide));
        OnPropertyChanged(nameof(IsInline));
        _settings.Settings.LastDiffLayout = value;
        _settings.Save();

        // Keep the current hunk in view after switching layout.
        if (_currentHunkIndex >= 0 && _currentHunkIndex < ActiveAnchors.Count)
            ScrollToRequested?.Invoke(ActiveAnchors[_currentHunkIndex]);
    }

    [RelayCommand]
    private void ToggleLayout() =>
        Layout = Layout == DiffLayout.SideBySide ? DiffLayout.Inline : DiffLayout.SideBySide;

    partial void OnShowWholeFileChanged(bool value)
    {
        OnPropertyChanged(nameof(WholeFileToggleLabel));
        _settings.Settings.ShowWholeFileDiff = value;
        _settings.Save();

        // Rebuild the currently displayed file under the new windowing mode.
        if (_lastBaseSha is { } sha && _lastChange is { } change)
            _ = LoadAsync(sha, change);
    }

    /// <summary>Moves to the next hunk; returns false if already at the last one (FR-24, FR-27).</summary>
    public bool GoToNextHunk()
    {
        var anchors = ActiveAnchors;
        if (_currentHunkIndex < anchors.Count - 1)
        {
            _currentHunkIndex++;
            ScrollToRequested?.Invoke(anchors[_currentHunkIndex]);
            return true;
        }
        return false;
    }

    /// <summary>Moves to the previous hunk; returns false if already at the first one.</summary>
    public bool GoToPreviousHunk()
    {
        var anchors = ActiveAnchors;
        if (_currentHunkIndex > 0)
        {
            _currentHunkIndex--;
            ScrollToRequested?.Invoke(anchors[_currentHunkIndex]);
            return true;
        }
        return false;
    }

    public void Clear()
    {
        _loadGeneration++; // abort any in-flight progressive population
        InlineRows.Clear();
        SideRows.Clear();
        _inlineAnchors.Clear();
        _sideAnchors.Clear();
        _currentHunkIndex = -1;
        _lastBaseSha = null;
        _lastChange = null;
        HasContent = false;
        IsBinary = false;
        IsTooLarge = false;
        HasChanges = false;
        FileName = null;
        FilePath = null;
        Message = "Select a file to view its changes.";
        RaiseShowState();
    }

    // The last successfully requested load, so a full-file toggle can rebuild it.
    private string? _lastBaseSha;
    private FileChange? _lastChange;

    // Bumped on every load (and on Clear); lets an in-flight progressive
    // population detect that a newer request has superseded it and bail out.
    private int _loadGeneration;

    /// <summary>One hunk's rows for both layouts, built off the UI thread.</summary>
    private sealed record BuiltHunk(
        List<object> InlineRows, object InlineAnchor,
        List<object> SideRows, object SideAnchor);

    public async Task LoadAsync(string baseSha, FileChange change, HunkPosition position = HunkPosition.First)
    {
        var gen = ++_loadGeneration;
        _lastBaseSha = baseSha;
        _lastChange = change;
        FileName = System.IO.Path.GetFileName(change.Path);
        FilePath = change.Path;
        var wholeFile = ShowWholeFile;

        // Fetch content and resolve syntax highlighting off the UI thread; this
        // also sets up the lazy hunk sequence (no diffing happens yet).
        var stream = await Task.Run(() => _diff.BuildDiffStream(baseSha, change, wholeFile: wholeFile));

        if (gen != _loadGeneration)
            return; // a newer load superseded this one

        await ApplyStreamAsync(stream, position, gen);
    }

    /// <summary>
    /// Consumes the hunk stream progressively: each hunk is diffed and turned into
    /// rows on a worker thread, then appended on the UI thread. The diff for later
    /// hunks therefore overlaps with rendering of earlier ones, so the top of a
    /// large file is visible while the bottom is still being computed.
    /// </summary>
    private async Task ApplyStreamAsync(DiffStream stream, HunkPosition position, int gen)
    {
        InlineRows.Clear();
        SideRows.Clear();
        _inlineAnchors.Clear();
        _sideAnchors.Clear();
        _currentHunkIndex = -1;

        IsBinary = stream.IsBinary;
        IsTooLarge = stream.IsTooLarge;
        HasContent = true;
        HasChanges = false; // not known until the first hunk arrives

        if (stream.IsBinary)
        {
            Message = "Binary file — no text diff.";
            RaiseShowState();
            return;
        }

        if (stream.IsTooLarge)
        {
            Message = "File is too large to display.";
            RaiseShowState();
            return;
        }

        var enumerator = stream.Hunks.GetEnumerator();
        try
        {
            var any = false;
            var sinceYield = 0;
            while (true)
            {
                // The expensive per-hunk diff + row build happen off the UI thread.
                var built = await Task.Run(() => AdvanceAndBuild(enumerator));
                if (gen != _loadGeneration)
                    return;
                if (built is null)
                    break;

                if (!any)
                {
                    any = true;
                    HasChanges = true;
                    RaiseShowState();
                }

                foreach (var row in built.InlineRows) InlineRows.Add(row);
                foreach (var row in built.SideRows) SideRows.Add(row);
                _inlineAnchors.Add(built.InlineAnchor);
                _sideAnchors.Add(built.SideAnchor);

                // Land on the first hunk the moment it appears.
                if (_currentHunkIndex < 0 && position == HunkPosition.First)
                {
                    _currentHunkIndex = 0;
                    ScrollToRequested?.Invoke(ActiveAnchors[0]);
                }

                // Periodically yield so layout/render can run between hunks.
                sinceYield += built.InlineRows.Count;
                if (sinceYield >= 400)
                {
                    sinceYield = 0;
                    await Task.Yield();
                    if (gen != _loadGeneration)
                        return;
                }
            }

            if (!any)
            {
                Message = "No textual differences.";
                RaiseShowState();
                return;
            }

            if (position == HunkPosition.Last && ActiveAnchors.Count > 0)
            {
                _currentHunkIndex = ActiveAnchors.Count - 1;
                ScrollToRequested?.Invoke(ActiveAnchors[_currentHunkIndex]);
            }
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    private static BuiltHunk? AdvanceAndBuild(IEnumerator<DiffHunk> hunks)
    {
        if (!hunks.MoveNext())
            return null;

        var hunk = hunks.Current;
        var (inlineRows, inlineAnchor) = BuildInlineHunk(hunk);
        var (sideRows, sideAnchor) = BuildSideHunk(hunk);
        return new BuiltHunk(inlineRows, inlineAnchor, sideRows, sideAnchor);
    }

    private void RaiseShowState()
    {
        OnPropertyChanged(nameof(ShowDiff));
        OnPropertyChanged(nameof(ShowMessage));
    }

    // --- Row building -------------------------------------------------------

    private static (List<object> Rows, object Anchor) BuildInlineHunk(DiffHunk hunk)
    {
        var rows = new List<object>(hunk.Lines.Count + 1);
        var separator = new HunkSeparatorRow { Header = hunk.Header };
        rows.Add(separator);
        foreach (var line in hunk.Lines)
        {
            rows.Add(new InlineDiffRow
            {
                OldNumber = line.OldLineNumber?.ToString() ?? string.Empty,
                NewNumber = line.NewLineNumber?.ToString() ?? string.Empty,
                Marker = line.Kind switch
                {
                    DiffLineKind.Added => "+",
                    DiffLineKind.Removed => "−",
                    _ => " ",
                },
                Kind = line.Kind,
                Segments = ToSegments(line),
            });
        }
        return (rows, separator);
    }

    private static (List<object> Rows, object Anchor) BuildSideHunk(DiffHunk hunk)
    {
        var rows = new List<object>(hunk.Lines.Count + 1);
        var separator = new HunkSeparatorRow { Header = hunk.Header };
        rows.Add(separator);

        var lines = hunk.Lines;
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind == DiffLineKind.Context)
            {
                rows.Add(new SideDiffRow
                {
                    Left = Cell(lines[i], lines[i].OldLineNumber),
                    Right = Cell(lines[i], lines[i].NewLineNumber),
                });
                i++;
                continue;
            }

            // Pair a run of removed lines with the following run of added lines.
            var rStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed) i++;
            var rEnd = i;
            var aStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Added) i++;
            var aEnd = i;

            var rCount = rEnd - rStart;
            var aCount = aEnd - aStart;
            var max = Math.Max(rCount, aCount);

            for (var k = 0; k < max; k++)
            {
                var left = k < rCount ? Cell(lines[rStart + k], lines[rStart + k].OldLineNumber) : SideCell.Empty;
                var right = k < aCount ? Cell(lines[aStart + k], lines[aStart + k].NewLineNumber) : SideCell.Empty;
                rows.Add(new SideDiffRow { Left = left, Right = right });
            }
        }
        return (rows, separator);
    }

    private static SideCell Cell(DiffLine line, int? number) => new()
    {
        IsEmpty = false,
        Number = number?.ToString() ?? string.Empty,
        Kind = line.Kind,
        Segments = ToSegments(line),
    };

    /// <summary>
    /// Combines the two independent segmentations of a line — word-level diff
    /// (background) and syntax tokens (foreground) — into render runs that are
    /// constant in both (FR-20, FR-23).
    /// </summary>
    private static IReadOnlyList<RenderSegment> ToSegments(DiffLine line)
    {
        var text = line.Text;
        if (text.Length == 0)
            return new[] { new RenderSegment(string.Empty, WordSegmentKind.Unchanged, null) };

        var kinds = new WordSegmentKind[text.Length];
        if (line.Segments is { } segs)
        {
            var pos = 0;
            foreach (var s in segs)
                for (var k = 0; k < s.Text.Length && pos < text.Length; k++, pos++)
                    kinds[pos] = s.Kind;
        }

        var colors = new string?[text.Length];
        if (line.Foreground is { } spans)
        {
            foreach (var span in spans)
            {
                if (span.Foreground is null)
                    continue;
                var end = Math.Min(span.Start + span.Length, text.Length);
                for (var k = Math.Max(0, span.Start); k < end; k++)
                    colors[k] = span.Foreground;
            }
        }

        var result = new List<RenderSegment>();
        var i = 0;
        while (i < text.Length)
        {
            var j = i + 1;
            while (j < text.Length && kinds[j] == kinds[i] && colors[j] == colors[i])
                j++;
            result.Add(new RenderSegment(text.Substring(i, j - i), kinds[i], colors[i]));
            i = j;
        }

        return result;
    }
}
