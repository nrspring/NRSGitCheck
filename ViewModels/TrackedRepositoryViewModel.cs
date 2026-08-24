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

    /// <summary>Guards the branch picker while a refresh writes the checked-out branch into it.</summary>
    private bool _applyingStatus;

    public TrackedRepositoryViewModel(
        RepositoriesViewModel owner,
        IGitCommandService gitCommands,
        IRepositoryStatusService statusService,
        TrackedRepository model)
    {
        _owner = owner;
        _gitCommands = gitCommands;
        _statusService = statusService;

        Path = model.Path;
        _name = string.IsNullOrWhiteSpace(model.Name) ? model.Path : model.Name;
    }

    /// <summary>Absolute path to the repository working directory; the row's identity.</summary>
    public string Path { get; }

    [ObservableProperty]
    private string _name;

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

    [ObservableProperty]
    private string _uncommittedText = string.Empty;

    [ObservableProperty]
    private bool _hasUnpushedCommits;

    [ObservableProperty]
    private string _unpushedText = string.Empty;

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

            IsBehind = status.BehindBy > 0;
            BehindText = status.BehindBy == 1 ? "1 commit behind" : $"{status.BehindBy} commits behind";

            IsClean = status.IsValid && !status.HasUncommittedChanges && !status.HasUnpushedCommits;
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

    // --- Row actions --------------------------------------------------------

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

    private void NotifyCommands()
    {
        SwitchToMainCommand.NotifyCanExecuteChanged();
        PullMainCommand.NotifyCanExecuteChanged();
    }
}
