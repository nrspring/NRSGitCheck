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
/// The create-branch dialog. With a pattern configured it shows one field per
/// <c>{Token}</c>, each seeded from that token's expression and editable; without one
/// it just asks for a branch name. Creating checks the new branch out in every
/// targeted repository, so the same feature branch can be cut across several at once.
/// </summary>
public partial class NewBranchViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IExpressionEvaluator _evaluator;
    private readonly IGitCommandService _gitCommands;

    /// <summary>The rows the branch is being created in.</summary>
    private IReadOnlyList<TrackedRepositoryViewModel> _targets = Array.Empty<TrackedRepositoryViewModel>();

    public NewBranchViewModel(
        ISettingsService settings,
        IExpressionEvaluator evaluator,
        IGitCommandService gitCommands)
    {
        _settings = settings;
        _evaluator = evaluator;
        _gitCommands = gitCommands;
    }

    /// <summary>Whether the dialog is showing.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>The repository the branch will be created in, or a count when there are several.</summary>
    [ObservableProperty]
    private string _repositoryName = string.Empty;

    /// <summary>The branch the new one will be cut from. Only meaningful for a single target.</summary>
    [ObservableProperty]
    private string _sourceBranch = string.Empty;

    /// <summary>Whether more than one repository is targeted — the header switches to a list.</summary>
    [ObservableProperty]
    private bool _hasMultipleTargets;

    /// <summary>One row per targeted repository, each with the branch it starts from.</summary>
    public ObservableCollection<NewBranchTargetViewModel> Targets { get; } = new();

    /// <summary>The configured pattern, shown as a reminder of the shape being filled in.</summary>
    [ObservableProperty]
    private string _pattern = string.Empty;

    /// <summary>Whether a pattern is configured; without one the dialog takes a plain name.</summary>
    [ObservableProperty]
    private bool _hasPattern;

    /// <summary>One field per token in the pattern.</summary>
    public ObservableCollection<BranchTokenInputViewModel> Tokens { get; } = new();

    /// <summary>The name that will be created: assembled from the fields, or typed directly.</summary>
    [ObservableProperty]
    private string _branchName = string.Empty;

    partial void OnBranchNameChanged(string value) => CreateCommand.NotifyCanExecuteChanged();

    /// <summary>A failure from Git, or from a default expression that could not run.</summary>
    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => CreateCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Opens the dialog for one or more repositories, evaluating each token's default
    /// expression to seed its field. An expression that fails leaves its field empty
    /// and says why, rather than blocking the whole dialog. The pattern's tokens are
    /// shared across every target — only per-repository state (name, current branch)
    /// varies.
    /// </summary>
    public async Task OpenAsync(IReadOnlyList<TrackedRepositoryViewModel> repositories)
    {
        if (repositories.Count == 0)
            return;

        SetTargets(repositories);
        Error = null;
        IsBusy = false;

        Pattern = _settings.Settings.NewBranchPattern ?? string.Empty;
        var names = BranchPattern.ParseTokens(Pattern);
        HasPattern = names.Count > 0;

        foreach (var token in Tokens)
            token.ValueChanged -= UpdateBranchName;
        Tokens.Clear();

        BranchName = string.Empty;
        IsVisible = true;

        if (!HasPattern)
            return;

        var defaults = _settings.Settings.BranchTokenDefaults;
        foreach (var name in names)
        {
            defaults.TryGetValue(name, out var code);
            var result = await _evaluator.EvaluateAsync(code);

            var token = new BranchTokenInputViewModel(
                name,
                result.Success ? result.Value : string.Empty,
                result.Success ? null : result.Error);

            token.ValueChanged += UpdateBranchName;
            Tokens.Add(token);
        }

        UpdateBranchName();
    }

    /// <summary>Refreshes the header and target list from the current set of targets.</summary>
    private void SetTargets(IReadOnlyList<TrackedRepositoryViewModel> repositories)
    {
        _targets = repositories;
        HasMultipleTargets = repositories.Count > 1;
        RepositoryName = repositories.Count == 1 ? repositories[0].Name : $"{repositories.Count} repositories";
        SourceBranch = repositories.Count == 1 ? repositories[0].CurrentBranch : string.Empty;

        Targets.Clear();
        foreach (var repo in repositories)
            Targets.Add(new NewBranchTargetViewModel(repo.Name, repo.CurrentBranch));
    }

    [RelayCommand]
    private void Cancel()
    {
        IsVisible = false;
        _targets = Array.Empty<TrackedRepositoryViewModel>();
        Targets.Clear();
    }

    private bool CanCreate() => !IsBusy && !string.IsNullOrWhiteSpace(BranchName) && _targets.Count > 0;

    /// <summary>
    /// Creates the branch and checks it out in every targeted repository, one after
    /// another so credential prompts stay comprehensible. Git validates the name, so
    /// a duplicate or an illegal ref comes back as a per-repository failure rather
    /// than aborting the rest. When every target succeeds the dialog closes; when any
    /// fail it stays open, narrowed to just the repositories still needing the branch,
    /// so fixing the name and retrying does not try to recreate ones that already
    /// have it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        if (_targets.Count == 0)
            return;

        var name = BranchName.Trim();
        var total = _targets.Count;
        Error = null;
        IsBusy = true;
        try
        {
            var succeeded = new List<TrackedRepositoryViewModel>();
            var failures = new List<(TrackedRepositoryViewModel Repo, string Message)>();

            foreach (var repo in _targets)
            {
                var result = await _gitCommands.CreateBranchAsync(repo.Path, name);
                if (result.Success)
                {
                    succeeded.Add(repo);
                    await repo.RefreshAsync();
                }
                else
                {
                    failures.Add((repo, result.Message));
                }
            }

            // The dialog is about to lose track of these; a repo that got the branch
            // no longer belongs in a "still selected for a bulk branch" state.
            foreach (var repo in succeeded)
                repo.IsSelected = false;

            if (succeeded.Count > 0)
            {
                Created?.Invoke(succeeded.Count == 1
                    ? $"{succeeded[0].Name}: created {name}."
                    : $"Created {name} in {succeeded.Count} of {total} repositories.");
            }

            if (failures.Count == 0)
            {
                IsVisible = false;
                _targets = Array.Empty<TrackedRepositoryViewModel>();
                Targets.Clear();
                return;
            }

            SetTargets(failures.Select(f => f.Repo).ToList());
            Error = failures.Count == 1
                ? failures[0].Message
                : string.Join(Environment.NewLine, failures.Select(f => $"{f.Repo.Name}: {f.Message}"));

            Failed?.Invoke(succeeded.Count == 0
                ? $"Could not create {name} in {(failures.Count == 1 ? failures[0].Repo.Name : $"{failures.Count} repositories")}."
                : $"{failures.Count} of {total} repositories failed.");
        }
        catch (Exception ex)
        {
            Error = $"Could not create the branch: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Raised with a status message once the branch has been created somewhere.</summary>
    public event Action<string>? Created;

    /// <summary>Raised with a summary when creation failed in at least one repository.</summary>
    public event Action<string>? Failed;

    private void UpdateBranchName() =>
        BranchName = BranchPattern.Build(
            Pattern,
            Tokens.ToDictionary(t => t.Name, t => t.Value, StringComparer.OrdinalIgnoreCase));
}
