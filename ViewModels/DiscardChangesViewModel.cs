using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// The discard confirmation, opened from a repository row's uncommitted-changes
/// pill. Nothing runs until the user presses Discard: the dialog exists to show
/// exactly what is about to be destroyed. Reverting tracked files and deleting
/// untracked ones are separate — the second is opt-in, because those files have
/// never been in Git and no other copy of them exists.
/// </summary>
public partial class DiscardChangesViewModel : ViewModelBase
{
    private TrackedRepositoryViewModel? _target;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _repositoryName = string.Empty;

    /// <summary>The branch whose committed state everything will be put back to.</summary>
    [ObservableProperty]
    private string _branch = string.Empty;

    /// <summary>What reverting the tracked files covers, e.g. "9 tracked files".</summary>
    [ObservableProperty]
    private string _trackedSummary = string.Empty;

    /// <summary>Whether there are untracked files at all; without any, the checkbox is pointless.</summary>
    [ObservableProperty]
    private bool _hasUntrackedFiles;

    /// <summary>Label for the opt-in, naming how many files would be deleted outright.</summary>
    [ObservableProperty]
    private string _untrackedSummary = string.Empty;

    /// <summary>Opt-in to <c>git clean</c>. Off on every open — this never remembers a yes.</summary>
    [ObservableProperty]
    private bool _deleteUntrackedFiles;

    public ObservableCollection<WorkingTreeChange> Changes { get; } = new();

    [ObservableProperty]
    private bool _hasUnlistedChanges;

    [ObservableProperty]
    private string _unlistedChangesText = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => DiscardCommand.NotifyCanExecuteChanged();

    /// <summary>Raised with a status message once changes have been thrown away.</summary>
    public event Action<string>? Discarded;

    /// <summary>Raised when Git refused the discard.</summary>
    public event Action<string>? Failed;

    public void Open(TrackedRepositoryViewModel repository)
    {
        _target = repository;
        RepositoryName = repository.Name;
        Branch = repository.CurrentBranch;
        Error = null;
        IsBusy = false;

        // Never carry a previous yes into a new confirmation.
        DeleteUntrackedFiles = false;

        var status = repository.Status;
        var tracked = status?.TrackedChangeCount ?? 0;
        var untracked = status?.UntrackedCount ?? 0;

        TrackedSummary = tracked == 1
            ? "1 tracked file will be put back to its committed state"
            : $"{tracked} tracked files will be put back to their committed state";

        HasUntrackedFiles = untracked > 0;
        UntrackedSummary = untracked == 1
            ? "Also delete 1 untracked file"
            : $"Also delete {untracked} untracked files";

        Changes.Clear();
        if (status is not null)
            foreach (var change in status.Changes)
                Changes.Add(change);

        var count = status?.UncommittedCount ?? 0;
        HasUnlistedChanges = status?.HasUnlistedChanges ?? false;
        var unlisted = count - Changes.Count;
        UnlistedChangesText = unlisted == 1 ? "…and 1 more" : $"…and {unlisted} more";

        IsVisible = true;

        // The command's CanExecute turns on the target that was just set; without
        // this the button keeps the answer it cached while there was none.
        DiscardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsVisible = false;
        _target = null;
        Changes.Clear();
    }

    private bool CanDiscard() => !IsBusy && _target is not null;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task Discard()
    {
        if (_target is not { } repository)
            return;

        Error = null;
        IsBusy = true;
        try
        {
            var result = await repository.DiscardChangesAsync(DeleteUntrackedFiles);
            if (!result.Success)
            {
                Error = result.Message;
                Failed?.Invoke($"{repository.Name}: {result.Message}");
                return;
            }

            Discarded?.Invoke($"{repository.Name}: {result.Message}");
            IsVisible = false;
            _target = null;
            Changes.Clear();
        }
        catch (Exception ex)
        {
            Error = $"Could not discard the changes: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
