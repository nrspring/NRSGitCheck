namespace NRSGitCheck.Models;

/// <summary>
/// One uncommitted path in a tracked repository, as listed in the commit and
/// discard dialogs so the user can see exactly what an action will touch.
/// Untracked directories are reported as the directory itself rather than walked,
/// matching how the status sweep counts them.
/// </summary>
public sealed record WorkingTreeChange(string Path, ChangeKind Kind)
{
    /// <summary>Never committed before, so discarding it means deleting the file.</summary>
    public bool IsUntracked => Kind == ChangeKind.Untracked;

    /// <summary>Single-letter tag for the row's badge, matching the review tab.</summary>
    public string Marker => Kind switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Modified => "M",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Untracked => "U",
        _ => "?",
    };
}
