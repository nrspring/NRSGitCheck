using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// One line in the branch list: the branch, when it last had a commit, its pull
/// request if the GitHub CLI can say, and a two-step delete. A delete Git refuses
/// because the branch is not fully merged turns into a second, explicit "delete
/// anyway" rather than failing outright — squash-merged PRs land there routinely.
/// </summary>
public partial class BranchItemViewModel : ViewModelBase
{
    private readonly BranchListViewModel _owner;

    public BranchItemViewModel(BranchListViewModel owner, LocalBranchInfo info, DateTimeOffset now)
    {
        _owner = owner;
        Name = info.Name;
        IsCurrent = info.IsCurrent;
        IsMainBranch = info.IsMainBranch;
        IsMergedIntoMain = info.IsMergedIntoMain;
        IsUpstreamGone = info.IsUpstreamGone;
        LastCommitText = info.LastCommitDate is { } date ? FormatAge(date, now) : "no commits";
        LastCommitToolTip = info.LastCommitDate is { } d ? d.LocalDateTime.ToString("f") : string.Empty;
    }

    public string Name { get; }

    public bool IsCurrent { get; }

    public bool IsMainBranch { get; }

    /// <summary>Every commit is already in main — Git's own view, no GitHub needed.</summary>
    public bool IsMergedIntoMain { get; }

    /// <summary>The remote branch it tracked has been deleted.</summary>
    public bool IsUpstreamGone { get; }

    /// <summary>"3 days ago" and so on; the exact time is in <see cref="LastCommitToolTip"/>.</summary>
    public string LastCommitText { get; }

    public string LastCommitToolTip { get; }

    // --- Pull request -------------------------------------------------------

    /// <summary>The pull request this branch belongs to, once known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPullRequest), nameof(PullRequestText), nameof(PullRequestStateText),
        nameof(PullRequestToolTip), nameof(IsPullRequestMerged), nameof(IsPullRequestOpen),
        nameof(IsPullRequestClosed), nameof(ShowMergedIntoMain), nameof(ForceDeleteExplanation))]
    private PullRequestInfo? _pullRequest;

    /// <summary>What to show in the PR column when there is no pull request to name.</summary>
    [ObservableProperty]
    private string _pullRequestPlaceholder = "—";

    public bool HasPullRequest => PullRequest is not null;

    public string PullRequestText => PullRequest is { } pr ? $"#{pr.Number}" : string.Empty;

    public string PullRequestStateText => PullRequest switch
    {
        { State: PullRequestState.Merged } => "Merged",
        { State: PullRequestState.Closed } => "Closed",
        { IsDraft: true } => "Draft",
        { } => "Open",
        _ => string.Empty,
    };

    public string PullRequestToolTip => PullRequest is { } pr
        ? $"#{pr.Number} {pr.Title}{Environment.NewLine}{pr.Url}"
        : string.Empty;

    public bool IsPullRequestMerged => PullRequest?.State == PullRequestState.Merged;

    public bool IsPullRequestOpen => PullRequest?.State == PullRequestState.Open;

    public bool IsPullRequestClosed => PullRequest?.State == PullRequestState.Closed;

    /// <summary>The "in main" hint, which a merged PR already says more clearly.</summary>
    public bool ShowMergedIntoMain => IsMergedIntoMain && !IsPullRequestMerged;

    // --- Delete -------------------------------------------------------------

    /// <summary>
    /// The checked-out branch cannot be deleted, and main is kept because the other
    /// actions on the tab depend on it; for those two the button is not shown at all.
    /// </summary>
    public bool CanDelete => !IsCurrent && !IsMainBranch;

    /// <summary>First step: "Delete this branch?" is showing.</summary>
    [ObservableProperty]
    private bool _isConfirmingDelete;

    /// <summary>Second step: Git refused a safe delete, and only a force delete would work.</summary>
    [ObservableProperty]
    private bool _needsForceDelete;

    [ObservableProperty]
    private string? _deleteError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginDeleteCommand), nameof(ConfirmDeleteCommand), nameof(ForceDeleteCommand))]
    private bool _isBusy;

    /// <summary>What a force delete would lose, said as plainly as what is known allows.</summary>
    public string ForceDeleteExplanation => IsPullRequestMerged
        ? $"Git does not see these commits in main — expected after a squash or rebase merge. PR #{PullRequest!.Number} was merged, so the work is on GitHub."
        : "This branch has commits that are not in main or in its upstream. Deleting it anyway removes them from this clone.";

    private bool CanBeginDelete() => CanDelete && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginDelete))]
    private void BeginDelete()
    {
        DeleteError = null;
        NeedsForceDelete = false;
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsConfirmingDelete = false;
        NeedsForceDelete = false;
        DeleteError = null;
    }

    [RelayCommand(CanExecute = nameof(CanBeginDelete))]
    private Task ConfirmDelete() => _owner.DeleteAsync(this, force: false);

    [RelayCommand(CanExecute = nameof(CanBeginDelete))]
    private Task ForceDelete() => _owner.DeleteAsync(this, force: true);

    /// <summary>
    /// "today", "yesterday", "5 days ago", "3 weeks ago", "4 months ago", "2 years ago".
    /// Coarse on purpose — the list is for spotting stale branches, and the exact time
    /// is a hover away.
    /// </summary>
    internal static string FormatAge(DateTimeOffset date, DateTimeOffset now)
    {
        var days = (int)Math.Floor((now.Date - date.ToOffset(now.Offset).Date).TotalDays);

        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 14 => $"{days} days ago",
            < 60 => $"{days / 7} weeks ago",
            < 365 => $"{days / 30} months ago",
            < 730 => "1 year ago",
            _ => $"{days / 365} years ago",
        };
    }
}
