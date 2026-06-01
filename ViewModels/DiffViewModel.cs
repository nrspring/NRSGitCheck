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

    [ObservableProperty]
    private string? _fileName;

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
        InlineRows.Clear();
        SideRows.Clear();
        _inlineAnchors.Clear();
        _sideAnchors.Clear();
        _currentHunkIndex = -1;
        HasContent = false;
        IsBinary = false;
        IsTooLarge = false;
        HasChanges = false;
        FileName = null;
        Message = "Select a file to view its changes.";
        RaiseShowState();
    }

    public async Task LoadAsync(string baseSha, FileChange change, HunkPosition position = HunkPosition.First)
    {
        FileName = System.IO.Path.GetFileName(change.Path);
        var doc = await Task.Run(() => _diff.BuildDiff(baseSha, change));
        Apply(doc, position);
    }

    private void Apply(DiffDocument doc, HunkPosition position)
    {
        InlineRows.Clear();
        SideRows.Clear();
        _inlineAnchors.Clear();
        _sideAnchors.Clear();
        _currentHunkIndex = -1;

        IsBinary = doc.IsBinary;
        IsTooLarge = doc.IsTooLarge;
        HasChanges = doc.HasChanges;
        HasContent = true;

        if (doc.IsBinary)
            Message = "Binary file — no text diff.";
        else if (doc.IsTooLarge)
            Message = "File is too large to display.";
        else if (!doc.HasChanges)
            Message = "No textual differences.";
        else
        {
            BuildInline(doc);
            BuildSide(doc);

            if (ActiveAnchors.Count > 0)
            {
                _currentHunkIndex = position == HunkPosition.Last ? ActiveAnchors.Count - 1 : 0;
                ScrollToRequested?.Invoke(ActiveAnchors[_currentHunkIndex]);
            }
        }

        RaiseShowState();
    }

    private void RaiseShowState()
    {
        OnPropertyChanged(nameof(ShowDiff));
        OnPropertyChanged(nameof(ShowMessage));
    }

    // --- Row building -------------------------------------------------------

    private void BuildInline(DiffDocument doc)
    {
        foreach (var hunk in doc.Hunks)
        {
            var separator = new HunkSeparatorRow { Header = hunk.Header };
            InlineRows.Add(separator);
            _inlineAnchors.Add(separator);
            foreach (var line in hunk.Lines)
            {
                InlineRows.Add(new InlineDiffRow
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
        }
    }

    private void BuildSide(DiffDocument doc)
    {
        foreach (var hunk in doc.Hunks)
        {
            var separator = new HunkSeparatorRow { Header = hunk.Header };
            SideRows.Add(separator);
            _sideAnchors.Add(separator);

            var lines = hunk.Lines;
            var i = 0;
            while (i < lines.Count)
            {
                if (lines[i].Kind == DiffLineKind.Context)
                {
                    SideRows.Add(new SideDiffRow
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
                    SideRows.Add(new SideDiffRow { Left = left, Right = right });
                }
            }
        }
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
