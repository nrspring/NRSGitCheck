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
    }

    [RelayCommand]
    private void ToggleLayout() =>
        Layout = Layout == DiffLayout.SideBySide ? DiffLayout.Inline : DiffLayout.SideBySide;

    public void Clear()
    {
        InlineRows.Clear();
        SideRows.Clear();
        HasContent = false;
        IsBinary = false;
        IsTooLarge = false;
        HasChanges = false;
        FileName = null;
        Message = "Select a file to view its changes.";
        RaiseShowState();
    }

    public async Task LoadAsync(string baseSha, FileChange change)
    {
        FileName = System.IO.Path.GetFileName(change.Path);
        var doc = await Task.Run(() => _diff.BuildDiff(baseSha, change));
        Apply(doc);
    }

    private void Apply(DiffDocument doc)
    {
        InlineRows.Clear();
        SideRows.Clear();

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
            InlineRows.Add(new HunkSeparatorRow { Header = hunk.Header });
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
            SideRows.Add(new HunkSeparatorRow { Header = hunk.Header });

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

    private static IReadOnlyList<RenderSegment> ToSegments(DiffLine line)
    {
        if (line.Segments is { } segments)
            return segments.Select(s => new RenderSegment(s.Text, s.Kind)).ToList();

        return new[] { new RenderSegment(line.Text, WordSegmentKind.Unchanged) };
    }
}
