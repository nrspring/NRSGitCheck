using System.IO;
using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// One row in the changed-files list (FR-12..17). Immutable presentation wrapper
/// around a <see cref="FileChange"/>.
/// </summary>
public sealed class FileChangeViewModel : ViewModelBase
{
    private readonly FileChange _model;

    public FileChangeViewModel(FileChange model) => _model = model;

    public string Path => _model.Path;
    public ChangeKind Kind => _model.Kind;
    public string? OldPath => _model.OldPath;
    public bool IsBinary => _model.IsBinary;
    public int LinesAdded => _model.LinesAdded;
    public int LinesDeleted => _model.LinesDeleted;

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
