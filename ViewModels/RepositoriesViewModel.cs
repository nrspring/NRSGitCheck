using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NRSGitCheck.Models;
using NRSGitCheck.Services;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// View model for the Repositories tab: a hand-curated list of repositories,
/// persisted in the user's settings, each showing its branch and whether it has
/// uncommitted or unpushed work, with per-row branch switching and pulls plus a
/// bulk pull across every repository sitting on main.
/// </summary>
public partial class RepositoriesViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IRepositoryStatusService _statusService;
    private readonly IGitCommandService _gitCommands;
    private readonly IFolderPickerService _folderPicker;

    /// <summary>Nesting count for in-flight operations, so overlapping rows keep the tab busy.</summary>
    private int _operationDepth;

    /// <summary>Whether the first status sweep has run; the tab does it lazily when opened.</summary>
    private bool _loaded;

    public RepositoriesViewModel(
        ISettingsService settings,
        IRepositoryStatusService statusService,
        IGitCommandService gitCommands,
        IFolderPickerService folderPicker)
    {
        _settings = settings;
        _statusService = statusService;
        _gitCommands = gitCommands;
        _folderPicker = folderPicker;

        foreach (var tracked in _settings.Settings.TrackedRepositories)
            Repositories.Add(CreateRow(tracked));

        HasRepositories = Repositories.Count > 0;
    }

    /// <summary>The pinned repositories, in the order they were added.</summary>
    public ObservableCollection<TrackedRepositoryViewModel> Repositories { get; } = new();

    [ObservableProperty]
    private bool _hasRepositories;

    [ObservableProperty]
    private string _status = "Add the repositories you want to keep an eye on.";

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True while any add, refresh, checkout, or pull is running.</summary>
    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => NotifyBulkCommands();

    // --- Loading and refreshing ---------------------------------------------

    /// <summary>
    /// Runs the first status sweep, once. The tab calls this when it is first
    /// opened so launching the app does not pay for reading every repository.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
            return;

        _loaded = true;
        await RefreshAllAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRunBulkOperation))]
    private Task RefreshAll() => RefreshAllAsync();

    /// <summary>Re-reads every repository's status, in parallel, off the UI thread.</summary>
    public async Task RefreshAllAsync()
    {
        if (Repositories.Count == 0)
        {
            Status = "Add the repositories you want to keep an eye on.";
            return;
        }

        ErrorMessage = null;
        BeginOperation("Reading repository status…");
        try
        {
            var rows = Repositories.ToList();
            var statuses = await _statusService.ReadAllAsync(rows.Select(r => r.Path));

            for (var i = 0; i < rows.Count; i++)
                rows[i].Apply(statuses[i]);

            Status = SummarizeList();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read repository status: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>A one-line roll-up of what the list currently looks like.</summary>
    private string SummarizeList()
    {
        var total = Repositories.Count;
        var dirty = Repositories.Count(r => r.HasUncommittedChanges);
        var unpushed = Repositories.Count(r => r.HasUnpushedCommits);
        var broken = Repositories.Count(r => !r.IsValid);

        var parts = new System.Collections.Generic.List<string>
        {
            total == 1 ? "1 repository" : $"{total} repositories",
        };

        if (dirty > 0)
            parts.Add($"{dirty} with uncommitted changes");
        if (unpushed > 0)
            parts.Add($"{unpushed} with unpushed commits");
        if (broken > 0)
            parts.Add($"{broken} unreadable");
        if (dirty == 0 && unpushed == 0 && broken == 0)
            parts.Add("all clean");

        return string.Join(" · ", parts);
    }

    // --- Add / remove -------------------------------------------------------

    /// <summary>
    /// Picks a folder and pins it. The path is validated first, so a folder that is
    /// not a Git repository is rejected rather than added as a permanently broken row.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunBulkOperation))]
    private async Task AddRepository()
    {
        var path = await _folderPicker.PickFolderAsync("Select a Git repository to track");
        if (string.IsNullOrEmpty(path))
            return;

        ErrorMessage = null;
        BeginOperation("Checking folder…");
        try
        {
            var status = await Task.Run(() => _statusService.Read(path));
            if (!status.IsValid)
            {
                ErrorMessage = $"{path}: {status.Error}";
                return;
            }

            if (!_settings.AddTrackedRepository(status.Path))
            {
                Status = $"{status.Name} is already in the list.";
                return;
            }

            // Take the entry back from settings so the row carries the same
            // normalized path that was persisted.
            var tracked = _settings.Settings.TrackedRepositories[^1];
            var row = CreateRow(tracked);
            Repositories.Add(row);
            HasRepositories = true;

            row.Apply(status with { Path = tracked.Path });
            Status = SummarizeList();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not add that folder: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Unpins a repository. Only this application's list is touched — nothing on disk.</summary>
    public void Remove(TrackedRepositoryViewModel repository)
    {
        _settings.RemoveTrackedRepository(repository.Path);
        Repositories.Remove(repository);
        HasRepositories = Repositories.Count > 0;
        ErrorMessage = null;
        Status = HasRepositories
            ? SummarizeList()
            : "Add the repositories you want to keep an eye on.";
        NotifyBulkCommands();
    }

    // --- Bulk pull ----------------------------------------------------------

    private bool CanRunBulkOperation() => !IsBusy;

    private bool CanPullAllOnMain() =>
        !IsBusy && Repositories.Any(r => r.IsValid && r.IsOnMainBranch);

    /// <summary>
    /// Pulls main in every repository that currently has main checked out. Repos on
    /// a feature branch are skipped rather than switched. The pulls run one after
    /// another so credential prompts and progress stay comprehensible.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPullAllOnMain))]
    private async Task PullAllOnMain()
    {
        var targets = Repositories.Where(r => r.IsValid && r.IsOnMainBranch).ToList();
        if (targets.Count == 0)
            return;

        ErrorMessage = null;
        BeginOperation($"Pulling main in {targets.Count} repositories…");
        try
        {
            var succeeded = 0;
            var failures = new System.Collections.Generic.List<string>();

            foreach (var repo in targets)
            {
                var result = await repo.PullMainAsync();
                if (result.Success)
                    succeeded++;
                else
                    failures.Add($"{repo.Name}: {result.Message}");
            }

            Status = failures.Count == 0
                ? $"Pulled {succeeded} of {targets.Count} repositories on main."
                : $"Pulled {succeeded} of {targets.Count}; {failures.Count} failed.";

            if (failures.Count > 0)
                ErrorMessage = string.Join("  ", failures);
        }
        finally
        {
            EndOperation();
        }
    }

    // --- Shared operation plumbing (used by the rows) -----------------------

    /// <summary>Marks the tab busy and shows what is happening. Pairs with <see cref="EndOperation"/>.</summary>
    public void BeginOperation(string status)
    {
        _operationDepth++;
        IsBusy = true;
        Status = status;
    }

    public void EndOperation()
    {
        _operationDepth = Math.Max(0, _operationDepth - 1);
        if (_operationDepth == 0)
            IsBusy = false;
    }

    /// <summary>Reports a successful step from a row.</summary>
    public void Report(string message)
    {
        ErrorMessage = null;
        Status = message;
    }

    /// <summary>Reports a failed step from a row, leaving the message on screen.</summary>
    public void ReportError(string message) => ErrorMessage = message;

    /// <summary>
    /// Called by a row once its status has been re-read: whether a bulk pull has
    /// anything to do depends on which repositories are sitting on main.
    /// </summary>
    public void NotifyRowStateChanged() => NotifyBulkCommands();

    private TrackedRepositoryViewModel CreateRow(TrackedRepository tracked) =>
        new(this, _gitCommands, _statusService, tracked);

    private void NotifyBulkCommands()
    {
        AddRepositoryCommand.NotifyCanExecuteChanged();
        RefreshAllCommand.NotifyCanExecuteChanged();
        PullAllOnMainCommand.NotifyCanExecuteChanged();
    }
}
