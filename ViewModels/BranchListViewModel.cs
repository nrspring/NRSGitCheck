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
/// The branch list, opened from a repository row. Local branches come from the
/// repository and show at once; pull requests come from the GitHub CLI afterwards,
/// and only when it is installed and signed in. Without it the list still works —
/// it just says where the PR column would come from.
/// </summary>
public partial class BranchListViewModel : ViewModelBase
{
    private readonly IBranchInfoService _branches;
    private readonly IGitHubCliService _gitHub;
    private readonly IGitCommandService _gitCommands;

    private TrackedRepositoryViewModel? _target;

    /// <summary>Bumped on every open, so a slow read for a list already closed is dropped.</summary>
    private int _openVersion;

    public BranchListViewModel(
        IBranchInfoService branches, IGitHubCliService gitHub, IGitCommandService gitCommands)
    {
        _branches = branches;
        _gitHub = gitHub;
        _gitCommands = gitCommands;
    }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _repositoryName = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    /// <summary>Why the PR column is empty, when it is: no gh, not signed in, or gh's own error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPullRequestNote))]
    private string? _pullRequestNote;

    public bool HasPullRequestNote => !string.IsNullOrEmpty(PullRequestNote);

    [ObservableProperty]
    private bool _isLoadingPullRequests;

    [ObservableProperty]
    private string _summary = string.Empty;

    public ObservableCollection<BranchItemViewModel> Branches { get; } = new();

    /// <summary>Raised with a status message after a branch is deleted.</summary>
    public event Action<string>? Deleted;

    /// <summary>Raised when Git refused a delete for a reason other than unmerged commits.</summary>
    public event Action<string>? Failed;

    /// <summary>
    /// Shows the list for one repository. <paramref name="gitHubStatus"/> is the
    /// tab's GitHub CLI check, which may still be running; branches are shown
    /// without waiting for it.
    /// </summary>
    public async Task OpenAsync(TrackedRepositoryViewModel repository, Task<GitHubCliStatus> gitHubStatus)
    {
        var version = ++_openVersion;

        _target = repository;
        RepositoryName = repository.Name;
        Branches.Clear();
        Error = null;
        PullRequestNote = null;
        IsLoadingPullRequests = false;
        Summary = string.Empty;
        IsLoading = true;
        IsVisible = true;

        IReadOnlyList<LocalBranchInfo> infos;
        try
        {
            infos = await Task.Run(() => _branches.ReadLocalBranches(repository.Path));
        }
        catch (Exception ex)
        {
            if (version == _openVersion)
            {
                Error = $"Could not read the branches: {ex.Message}";
                IsLoading = false;
            }
            return;
        }

        if (version != _openVersion)
            return;

        var now = DateTimeOffset.Now;
        foreach (var info in infos)
            Branches.Add(new BranchItemViewModel(this, info, now));

        IsLoading = false;
        UpdateSummary();

        await LoadPullRequestsAsync(repository, gitHubStatus, version);
    }

    private async Task LoadPullRequestsAsync(
        TrackedRepositoryViewModel repository, Task<GitHubCliStatus> gitHubStatus, int version)
    {
        GitHubCliStatus status;
        try
        {
            status = await gitHubStatus;
        }
        catch
        {
            status = GitHubCliStatus.NotInstalled;
        }

        if (version != _openVersion)
            return;

        switch (status)
        {
            case GitHubCliStatus.NotInstalled:
                PullRequestNote = "Install the GitHub CLI (gh) to see the pull request for each branch.";
                return;
            case GitHubCliStatus.NotSignedIn:
                PullRequestNote = "Run gh auth login to see the pull request for each branch.";
                return;
            case GitHubCliStatus.Unknown:
                return;
        }

        IsLoadingPullRequests = true;
        foreach (var branch in Branches)
            branch.PullRequestPlaceholder = "…";

        PullRequestListResult result;
        try
        {
            result = await _gitHub.ListPullRequestsAsync(repository.Path);
        }
        catch (Exception ex)
        {
            result = PullRequestListResult.Failed(ex.Message);
        }

        if (version != _openVersion)
            return;

        IsLoadingPullRequests = false;

        if (!result.Success)
        {
            PullRequestNote = $"Pull requests unavailable: {result.Error}";
            foreach (var branch in Branches)
                branch.PullRequestPlaceholder = "—";
            return;
        }

        foreach (var branch in Branches)
        {
            branch.PullRequest = PullRequestInfo.FindFor(branch.Name, result.PullRequests);
            branch.PullRequestPlaceholder = "No PR";
        }
    }

    [RelayCommand]
    private void Close()
    {
        _openVersion++;
        IsVisible = false;
        _target = null;
        Branches.Clear();
    }

    /// <summary>
    /// Runs a delete for one line. A safe delete Git refuses as "not fully merged"
    /// moves that line on to the force-delete question instead of reporting an error.
    /// </summary>
    internal async Task DeleteAsync(BranchItemViewModel branch, bool force)
    {
        if (_target is not { } repository || !branch.CanDelete)
            return;

        branch.IsBusy = true;
        branch.DeleteError = null;
        try
        {
            var result = await _gitCommands.DeleteBranchAsync(repository.Path, branch.Name, force);

            if (result.Success)
            {
                Branches.Remove(branch);
                UpdateSummary();
                Deleted?.Invoke($"{repository.Name}: {result.Message}");

                // The row's branch picker lists local branches; it should lose this one.
                await repository.RefreshAsync();
                return;
            }

            if (!force && result.IsNotFullyMerged)
            {
                branch.IsConfirmingDelete = false;
                branch.NeedsForceDelete = true;
                return;
            }

            branch.DeleteError = result.Message;
            Failed?.Invoke($"{repository.Name}: {result.Message}");
        }
        catch (Exception ex)
        {
            branch.DeleteError = $"Could not delete the branch: {ex.Message}";
        }
        finally
        {
            branch.IsBusy = false;
        }
    }

    private void UpdateSummary() =>
        Summary = Branches.Count == 1 ? "1 local branch" : $"{Branches.Count} local branches";
}
