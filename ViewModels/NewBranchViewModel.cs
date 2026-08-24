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
/// it just asks for a branch name. Creating checks the new branch out.
/// </summary>
public partial class NewBranchViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IExpressionEvaluator _evaluator;
    private readonly IGitCommandService _gitCommands;

    /// <summary>The row the branch is being created in.</summary>
    private TrackedRepositoryViewModel? _target;

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

    /// <summary>The repository the branch will be created in.</summary>
    [ObservableProperty]
    private string _repositoryName = string.Empty;

    /// <summary>The branch the new one will be cut from.</summary>
    [ObservableProperty]
    private string _sourceBranch = string.Empty;

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
    /// Opens the dialog for a repository, evaluating each token's default expression
    /// to seed its field. An expression that fails leaves its field empty and says why,
    /// rather than blocking the whole dialog.
    /// </summary>
    public async Task OpenAsync(TrackedRepositoryViewModel repository)
    {
        _target = repository;
        RepositoryName = repository.Name;
        SourceBranch = repository.CurrentBranch;
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

    [RelayCommand]
    private void Cancel()
    {
        IsVisible = false;
        _target = null;
    }

    private bool CanCreate() => !IsBusy && !string.IsNullOrWhiteSpace(BranchName);

    /// <summary>
    /// Creates the branch and checks it out. Git validates the name, so a duplicate or
    /// an illegal ref comes back as a message in the dialog rather than a silent failure.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        if (_target is not { } repository)
            return;

        var name = BranchName.Trim();
        Error = null;
        IsBusy = true;
        try
        {
            var result = await _gitCommands.CreateBranchAsync(repository.Path, name);
            if (!result.Success)
            {
                Error = result.Message;
                return;
            }

            await repository.RefreshAsync();
            Created?.Invoke($"{repository.Name}: {result.Message}");

            IsVisible = false;
            _target = null;
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

    /// <summary>Raised with a status message once a branch has been created.</summary>
    public event Action<string>? Created;

    private void UpdateBranchName() =>
        BranchName = BranchPattern.Build(
            Pattern,
            Tokens.ToDictionary(t => t.Name, t => t.Value, StringComparer.OrdinalIgnoreCase));
}
