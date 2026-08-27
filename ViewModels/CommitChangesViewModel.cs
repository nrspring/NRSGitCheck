using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// The commit dialog, opened from a repository row's uncommitted-changes pill. It
/// lists what is about to be committed and takes the message. Committing stages
/// everything first, so the list is the commit — there is no partial staging here.
/// </summary>
public partial class CommitChangesViewModel : ViewModelBase
{
    private TrackedRepositoryViewModel? _target;

    /// <summary>Whether the dialog is showing.</summary>
    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _repositoryName = string.Empty;

    /// <summary>The branch the commit will land on.</summary>
    [ObservableProperty]
    private string _branch = string.Empty;

    /// <summary>"12 changes will be committed" — the exact count, not the listed one.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>The changed paths, as far as the status sweep listed them.</summary>
    public ObservableCollection<WorkingTreeChange> Changes { get; } = new();

    /// <summary>Set when the list was capped, so the dialog can own up to it.</summary>
    [ObservableProperty]
    private bool _hasUnlistedChanges;

    [ObservableProperty]
    private string _unlistedChangesText = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    partial void OnMessageChanged(string value) => CommitCommand.NotifyCanExecuteChanged();

    /// <summary>Git's refusal, kept on screen so the message can be fixed and retried.</summary>
    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => CommitCommand.NotifyCanExecuteChanged();

    /// <summary>Raised with a status message once a commit lands.</summary>
    public event Action<string>? Committed;

    /// <summary>Raised when Git refused the commit.</summary>
    public event Action<string>? Failed;

    /// <summary>Opens the dialog for one repository, described by its last status read.</summary>
    public void Open(TrackedRepositoryViewModel repository)
    {
        _target = repository;
        RepositoryName = repository.Name;
        Branch = repository.CurrentBranch;
        Message = string.Empty;
        Error = null;
        IsBusy = false;

        var status = repository.Status;
        var count = status?.UncommittedCount ?? 0;
        Summary = count == 1 ? "1 change will be committed" : $"{count} changes will be committed";

        Changes.Clear();
        if (status is not null)
            foreach (var change in status.Changes)
                Changes.Add(change);

        HasUnlistedChanges = status?.HasUnlistedChanges ?? false;
        var unlisted = count - Changes.Count;
        UnlistedChangesText = unlisted == 1 ? "…and 1 more" : $"…and {unlisted} more";

        IsVisible = true;

        // Same as the discard dialog: re-ask now that there is a target again.
        CommitCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsVisible = false;
        _target = null;
        Changes.Clear();
    }

    private bool CanCommit() => !IsBusy && _target is not null && !string.IsNullOrWhiteSpace(Message);

    /// <summary>
    /// Stages everything and commits. A refusal from Git — no configured identity, a
    /// failing hook, nothing actually staged — leaves the dialog open with the
    /// message intact so it can be corrected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task Commit()
    {
        if (_target is not { } repository)
            return;

        Error = null;
        IsBusy = true;
        try
        {
            var result = await repository.CommitAllAsync(Message);
            if (!result.Success)
            {
                Error = result.Message;
                Failed?.Invoke($"{repository.Name}: {result.Message}");
                return;
            }

            Committed?.Invoke($"{repository.Name}: {result.Message}");
            IsVisible = false;
            _target = null;
            Changes.Clear();
        }
        catch (Exception ex)
        {
            Error = $"Could not commit: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
