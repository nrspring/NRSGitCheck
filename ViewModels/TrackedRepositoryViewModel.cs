using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NRSGitCheck.Models;
using NRSGitCheck.Services;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// One row on the Repositories tab: a pinned repository, its branch and dirty /
/// unpushed state, and the actions that can move it — switch branch, switch to
/// main, pull main.
/// </summary>
public partial class TrackedRepositoryViewModel : ViewModelBase
{
    private readonly RepositoriesViewModel _owner;
    private readonly IGitCommandService _gitCommands;
    private readonly IRepositoryStatusService _statusService;
    private readonly IClipboardService _clipboard;

    /// <summary>Guards the branch picker while a refresh writes the checked-out branch into it.</summary>
    private bool _applyingStatus;

    public TrackedRepositoryViewModel(
        RepositoriesViewModel owner,
        IGitCommandService gitCommands,
        IRepositoryStatusService statusService,
        IClipboardService clipboard,
        TrackedRepository model)
    {
        _owner = owner;
        _gitCommands = gitCommands;
        _statusService = statusService;
        _clipboard = clipboard;

        Path = model.Path;
        _name = string.IsNullOrWhiteSpace(model.Name) ? model.Path : model.Name;
    }

    /// <summary>Absolute path to the repository working directory; the row's identity.</summary>
    public string Path { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>
    /// Checked in the row's checkbox to include this repository in a bulk action —
    /// currently, creating the same branch across several repositories at once.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner.NotifySelectionChanged();

    // --- Status -------------------------------------------------------------

    /// <summary>The last status read, or null until the first refresh completes.</summary>
    public RepositoryStatus? Status { get; private set; }

    [ObservableProperty]
    private string _currentBranch = string.Empty;

    [ObservableProperty]
    private bool _isValid = true;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _hasUncommittedChanges;

    partial void OnHasUncommittedChangesChanged(bool value) => NotifyCommands();

    [ObservableProperty]
    private string _uncommittedText = string.Empty;

    [ObservableProperty]
    private bool _hasUnpushedCommits;

    partial void OnHasUnpushedCommitsChanged(bool value) => NotifyCommands();

    [ObservableProperty]
    private string _unpushedText = string.Empty;

    /// <summary>The branch is not on the remote yet, so a push has to create it there.</summary>
    [ObservableProperty]
    private bool _needsFirstPush;

    partial void OnNeedsFirstPushChanged(bool value)
    {
        OnPropertyChanged(nameof(PushToOriginToolTip));
        NotifyCommands();
    }

    /// <summary>
    /// Explains which of the two pushes the button will do, since publishing a branch
    /// for the first time also changes what the branch tracks from then on.
    /// </summary>
    public string PushToOriginToolTip => NeedsFirstPush
        ? "This branch is not on origin yet. Pushing creates it there and sets it as the upstream."
        : "Send this branch's commits to its upstream. Never force-pushes — a push the remote rejects is reported as-is.";

    [ObservableProperty]
    private bool _isBehind;

    [ObservableProperty]
    private string _behindText = string.Empty;

    /// <summary>No uncommitted work and nothing waiting to be pushed.</summary>
    [ObservableProperty]
    private bool _isClean;

    /// <summary>Whether main is the checked-out branch — what "pull all" targets.</summary>
    [ObservableProperty]
    private bool _isOnMainBranch;

    /// <summary>Whether the repository has a remote at all — there is nothing to pull without one.</summary>
    [ObservableProperty]
    private bool _hasRemote;

    partial void OnHasRemoteChanged(bool value) => NotifyCommands();

    /// <summary>Local branches, offered in the row's branch picker.</summary>
    public ObservableCollection<string> LocalBranches { get; } = new();

    /// <summary>True while this row runs a checkout or a pull, so its buttons disable.</summary>
    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    /// <summary>The integration branch as Git reports it ("main", "origin/master", …).</summary>
    [ObservableProperty]
    private string? _mainBranchName;

    /// <summary>The local branch a "switch to main" would check out.</summary>
    private string? _localMainBranch;

    public string SwitchToMainLabel =>
        _localMainBranch is { Length: > 0 } main ? $"Switch to {main}" : "Switch to main";

    public string PullMainLabel =>
        _localMainBranch is { Length: > 0 } main ? $"Pull {main}" : "Pull main";

    /// <summary>
    /// Applies a freshly read status. The branch picker is written under a guard so
    /// re-selecting the checked-out branch does not look like a user-requested switch.
    /// </summary>
    public void Apply(RepositoryStatus status)
    {
        Status = status;

        _applyingStatus = true;
        try
        {
            Name = string.IsNullOrWhiteSpace(status.Name) ? Name : status.Name;
            IsValid = status.IsValid;
            Error = status.Error;
            CurrentBranch = status.IsValid ? status.CurrentBranch : string.Empty;

            // An invalid repository can't take a branch; drop it out of any bulk
            // selection rather than leaving a dead checkbox checked.
            if (!status.IsValid)
                IsSelected = false;

            LocalBranches.Clear();
            foreach (var branch in status.LocalBranches)
                LocalBranches.Add(branch);

            SelectedBranch = LocalBranches.Contains(status.CurrentBranch) ? status.CurrentBranch : null;

            HasRemote = status.HasRemote;
            MainBranchName = status.MainBranch;
            _localMainBranch = status.LocalMainBranch;
            IsOnMainBranch = status.IsOnMainBranch;

            HasUncommittedChanges = status.HasUncommittedChanges;
            UncommittedText = status.UncommittedCount == 1
                ? "1 uncommitted change"
                : $"{status.UncommittedCount} uncommitted changes";

            HasUnpushedCommits = status.HasUnpushedCommits;
            UnpushedText = status.AheadBy == 1 ? "1 unpushed commit" : $"{status.AheadBy} unpushed commits";
            NeedsFirstPush = status.NeedsFirstPush;

            IsBehind = status.BehindBy > 0;
            BehindText = status.BehindBy == 1 ? "1 commit behind" : $"{status.BehindBy} commits behind";

            IsClean = status.IsValid && !status.HasUncommittedChanges &&
                      !status.HasUnpushedCommits && !status.NeedsFirstPush;
        }
        finally
        {
            _applyingStatus = false;
        }

        OnPropertyChanged(nameof(SwitchToMainLabel));
        OnPropertyChanged(nameof(PullMainLabel));
        NotifyCommands();
        _owner.NotifyRowStateChanged();
    }

    // --- Branch switching ---------------------------------------------------

    /// <summary>
    /// The branch shown in the picker. Assigning it from the UI checks that branch
    /// out; a refresh writes it back under <see cref="_applyingStatus"/>.
    /// </summary>
    [ObservableProperty]
    private string? _selectedBranch;

    partial void OnSelectedBranchChanged(string? value)
    {
        if (_applyingStatus || IsBusy || string.IsNullOrEmpty(value))
            return;
        if (string.Equals(value, CurrentBranch, StringComparison.Ordinal))
            return;

        _ = SwitchToBranchAsync(value);
    }

    /// <summary>
    /// Checks out the chosen branch. A checkout Git refuses — typically because
    /// uncommitted work would be overwritten — is reported as-is, and the picker
    /// snaps back to whatever is actually checked out. Nothing is stashed or
    /// discarded on the user's behalf.
    /// </summary>
    public async Task SwitchToBranchAsync(string branch)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        _owner.BeginOperation($"Checking out {branch} in {Name}…");
        try
        {
            var result = await _gitCommands.CheckoutBranchAsync(Path, branch);
            await RefreshAsync();

            if (result.Success)
                _owner.Report($"{Name}: checked out {branch}.");
            else
                _owner.ReportError($"{Name}: {result.Message}");
        }
        catch (Exception ex)
        {
            _owner.ReportError($"{Name}: could not switch branch — {ex.Message}");
            await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
            _owner.EndOperation();
        }
    }

    private bool CanSwitchToMain() =>
        !IsBusy && IsValid && !IsOnMainBranch && _localMainBranch is { Length: > 0 } main &&
        LocalBranches.Contains(main);

    [RelayCommand(CanExecute = nameof(CanSwitchToMain))]
    private async Task SwitchToMain()
    {
        if (_localMainBranch is { Length: > 0 } main)
            await SwitchToBranchAsync(main);
    }

    // --- Pull main ----------------------------------------------------------

    private bool CanPullMain() =>
        !IsBusy && IsValid && HasRemote && MainBranchName is { Length: > 0 };

    [RelayCommand(CanExecute = nameof(CanPullMain))]
    private async Task PullMain()
    {
        var result = await PullMainAsync();
        if (result.Success)
            _owner.Report($"{Name}: {result.Message}");
        else
            _owner.ReportError($"{Name}: {result.Message}");
    }

    /// <summary>
    /// Fast-forwards this repository's main branch from its remote and re-reads the
    /// status. Returns the raw result so a bulk pull can tally outcomes itself.
    /// </summary>
    public async Task<GitCommandResult> PullMainAsync()
    {
        if (IsBusy)
            return new GitCommandResult(false, "Busy.");
        if (MainBranchName is not { Length: > 0 } mainBranch)
            return new GitCommandResult(false, "No main/master branch was found.");
        if (!HasRemote)
            return new GitCommandResult(false, "This repository has no remote to pull from.");

        IsBusy = true;
        _owner.BeginOperation($"Pulling {_localMainBranch ?? mainBranch} in {Name}…");
        try
        {
            var result = await _gitCommands.PullMainAsync(Path, mainBranch, CurrentBranch);
            await RefreshAsync();

            return result.Success && string.IsNullOrWhiteSpace(result.Message)
                ? new GitCommandResult(true, $"{mainBranch} is up to date.")
                : result;
        }
        catch (Exception ex)
        {
            return new GitCommandResult(false, $"Pull failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _owner.EndOperation();
        }
    }

    // --- Push ---------------------------------------------------------------

    private bool CanPush() => !IsBusy && IsValid && HasRemote && (HasUnpushedCommits || NeedsFirstPush);

    [RelayCommand(CanExecute = nameof(CanPush))]
    private async Task PushToOrigin()
    {
        var result = await PushAsync();
        if (result.Success)
            _owner.Report($"{Name}: {result.Message}");
        else
            _owner.ReportError($"{Name}: {result.Message}");
    }

    /// <summary>
    /// Pushes the checked-out branch and re-reads the status. A branch with no
    /// upstream is published to origin and starts tracking it; one that already has
    /// an upstream goes where it already points. Returns the raw result so a caller
    /// can tally outcomes itself.
    /// </summary>
    public async Task<GitCommandResult> PushAsync()
    {
        if (IsBusy)
            return new GitCommandResult(false, "Busy.");
        if (!HasRemote)
            return new GitCommandResult(false, "This repository has no remote to push to.");

        IsBusy = true;
        _owner.BeginOperation($"Pushing {CurrentBranch} in {Name}…");
        try
        {
            var result = await _gitCommands.PushAsync(Path, CurrentBranch, setUpstream: NeedsFirstPush);
            await RefreshAsync();
            return result;
        }
        catch (Exception ex)
        {
            await RefreshAsync();
            return new GitCommandResult(false, $"Push failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _owner.EndOperation();
        }
    }

    // --- Uncommitted changes ------------------------------------------------

    private bool CanActOnUncommittedChanges() => !IsBusy && IsValid && HasUncommittedChanges;

    /// <summary>Opens the commit dialog for this repository.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnUncommittedChanges))]
    private void CommitChanges() => _owner.BeginCommit(this);

    /// <summary>Opens the discard confirmation for this repository.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnUncommittedChanges))]
    private void DiscardChanges() => _owner.BeginDiscard(this);

    /// <summary>
    /// Stages everything and commits it, then re-reads the status. The raw result
    /// comes back so the dialog can keep a refusal on screen next to the message
    /// that caused it.
    /// </summary>
    public async Task<GitCommandResult> CommitAllAsync(string message)
    {
        if (IsBusy)
            return new GitCommandResult(false, "Busy.");

        IsBusy = true;
        _owner.BeginOperation($"Committing in {Name}…");
        try
        {
            var result = await _gitCommands.CommitAllAsync(Path, message);
            await RefreshAsync();
            return result;
        }
        catch (Exception ex)
        {
            await RefreshAsync();
            return new GitCommandResult(false, $"Commit failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _owner.EndOperation();
        }
    }

    /// <summary>
    /// Throws this repository's uncommitted work away. Only called from the discard
    /// confirmation — nothing here re-checks intent, so the caller must have it.
    /// </summary>
    public async Task<GitCommandResult> DiscardChangesAsync(bool deleteUntrackedFiles)
    {
        if (IsBusy)
            return new GitCommandResult(false, "Busy.");

        IsBusy = true;
        _owner.BeginOperation($"Discarding changes in {Name}…");
        try
        {
            var result = await _gitCommands.DiscardChangesAsync(Path, deleteUntrackedFiles);
            await RefreshAsync();
            return result;
        }
        catch (Exception ex)
        {
            await RefreshAsync();
            return new GitCommandResult(false, $"Discard failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _owner.EndOperation();
        }
    }

    // --- Row actions --------------------------------------------------------

    private bool CanCreateBranch() => !IsBusy && IsValid;

    /// <summary>Opens the create-branch dialog for just this repository.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateBranch))]
    private void NewBranch() => _owner.BeginNewBranch(new[] { this });

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    /// <summary>Re-reads this repository's status off the UI thread.</summary>
    public async Task RefreshAsync()
    {
        var status = await Task.Run(() => _statusService.Read(Path));
        Apply(status);
    }

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    /// <summary>Puts this repository's path on the clipboard.</summary>
    [RelayCommand]
    private async Task CopyPath()
    {
        if (await _clipboard.SetTextAsync(Path))
            _owner.Report($"Copied {Path}");
        else
            _owner.ReportError("Could not copy the path to the clipboard.");
    }

    private void NotifyCommands()
    {
        SwitchToMainCommand.NotifyCanExecuteChanged();
        PullMainCommand.NotifyCanExecuteChanged();
        NewBranchCommand.NotifyCanExecuteChanged();
        CommitChangesCommand.NotifyCanExecuteChanged();
        DiscardChangesCommand.NotifyCanExecuteChanged();
        PushToOriginCommand.NotifyCanExecuteChanged();
    }
}
