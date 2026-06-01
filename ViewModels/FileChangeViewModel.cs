using CommunityToolkit.Mvvm.ComponentModel;
using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// One row in the changed-files list (FR-12..17). Presentation wrapper around a
/// <see cref="FileChange"/>. Line counts and the binary flag start at their initial
/// values and are filled in by <see cref="ApplyStats"/> once the background stats
/// pass completes (NFR-1).
/// </summary>
public sealed partial class FileChangeViewModel : ViewModelBase
{
    private readonly FileChange _model;

    public FileChangeViewModel(FileChange model)
    {
        _model = model;
        _linesAdded = model.LinesAdded;
        _linesDeleted = model.LinesDeleted;
        _isBinary = model.IsBinary;
    }

    /// <summary>The underlying change, used to build the diff.</summary>
    public FileChange Model => _model;

    public string Path => _model.Path;
    public ChangeKind Kind => _model.Kind;
    public string? OldPath => _model.OldPath;

    [ObservableProperty]
    private int _linesAdded;

    [ObservableProperty]
    private int _linesDeleted;

    [ObservableProperty]
    private bool _isBinary;

    partial void OnLinesAddedChanged(int value)
    {
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(HasCounts));
    }

    partial void OnLinesDeletedChanged(int value)
    {
        OnPropertyChanged(nameof(DeletedText));
        OnPropertyChanged(nameof(HasCounts));
    }

    partial void OnIsBinaryChanged(bool value) => OnPropertyChanged(nameof(HasCounts));

    /// <summary>Applies the deferred line counts and binary flag from the stats pass.</summary>
    public void ApplyStats(FileStats stats)
    {
        LinesAdded = stats.LinesAdded;
        LinesDeleted = stats.LinesDeleted;
        IsBinary = stats.IsBinary;
    }

    public string FileName => System.IO.Path.GetFileName(_model.Path);

    /// <summary>Directory portion (forward-slashed), or null when at the repo root.</summary>
    public string? Directory
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(_model.Path);
            return string.IsNullOrEmpty(dir) ? null : dir.Replace('\\', '/');
        }
    }

    public bool IsRenamed => _model.Kind == ChangeKind.Renamed && !string.IsNullOrEmpty(_model.OldPath);

    /// <summary>Single-letter change badge (FR-13).</summary>
    public string BadgeText => _model.Kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Modified => "M",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Untracked => "U",
        _ => "?",
    };

    public bool HasCounts => !IsBinary && (LinesAdded > 0 || LinesDeleted > 0);
    public string AddedText => $"+{LinesAdded}";
    public string DeletedText => $"−{LinesDeleted}"; // minus sign
}
