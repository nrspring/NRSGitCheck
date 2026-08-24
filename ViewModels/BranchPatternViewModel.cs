using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NRSGitCheck.Models;
using NRSGitCheck.Services;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// The branch-pattern settings dialog: the <c>{Token}</c> pattern new branches are
/// named from, and the C# expression that seeds each token. Both are checked as they
/// are typed, so a pattern with an unclosed brace or an expression that does not
/// compile is caught here rather than when a branch is being created.
/// </summary>
public partial class BranchPatternViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IExpressionEvaluator _evaluator;

    /// <summary>Expressions for tokens that have dropped out of the pattern, kept in case they come back.</summary>
    private readonly Dictionary<string, string> _remembered = new(StringComparer.OrdinalIgnoreCase);

    public BranchPatternViewModel(ISettingsService settings, IExpressionEvaluator evaluator)
    {
        _settings = settings;
        _evaluator = evaluator;
        _pattern = settings.Settings.NewBranchPattern ?? string.Empty;
    }

    /// <summary>Whether the dialog is showing.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>The name pattern, for example <c>nrs/{Date}-sa-{SANumber}-{description}</c>.</summary>
    [ObservableProperty]
    private string _pattern;

    partial void OnPatternChanged(string value)
    {
        PatternError = BranchPattern.Validate(value);
        RebuildTokens();
        UpdatePreview();
    }

    /// <summary>What is wrong with the pattern itself, if anything.</summary>
    [ObservableProperty]
    private string? _patternError;

    partial void OnPatternErrorChanged(string? value) => OnPropertyChanged(nameof(HasPatternError));

    public bool HasPatternError => !string.IsNullOrEmpty(PatternError);

    /// <summary>One editor per token in the pattern, in the order they appear.</summary>
    public ObservableCollection<BranchTokenDefaultViewModel> Tokens { get; } = new();

    /// <summary>An example branch name built from what the expressions produce right now.</summary>
    [ObservableProperty]
    private string _preview = string.Empty;

    /// <summary>Opens the dialog on the saved pattern and expressions.</summary>
    public void Open()
    {
        Pattern = _settings.Settings.NewBranchPattern ?? string.Empty;

        foreach (var (name, code) in _settings.Settings.BranchTokenDefaults)
            _remembered[name] = code;

        PatternError = BranchPattern.Validate(Pattern);
        RebuildTokens();
        UpdatePreview();
        IsVisible = true;
    }

    [RelayCommand]
    private void Cancel() => IsVisible = false;

    /// <summary>
    /// Saves the pattern and every token's expression. Expressions that do not
    /// compile are saved too — a half-written default should not be lost — but the
    /// dialog says so, and the create-branch dialog reports it again if it stays broken.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        _settings.Settings.NewBranchPattern = Pattern?.Trim() ?? string.Empty;

        var defaults = _settings.Settings.BranchTokenDefaults;
        defaults.Clear();
        foreach (var token in Tokens.Where(t => !string.IsNullOrWhiteSpace(t.Code)))
            defaults[token.Name] = token.Code.Trim();

        _settings.Save();
        IsVisible = false;
        Saved?.Invoke();
    }

    /// <summary>Raised after a successful save so the owner can refresh anything derived from it.</summary>
    public event Action? Saved;

    /// <summary>
    /// Matches the editor list to the tokens in the pattern, keeping the expression
    /// already entered for a token that is still there (or was there earlier).
    /// </summary>
    private void RebuildTokens()
    {
        foreach (var token in Tokens)
        {
            _remembered[token.Name] = token.Code;
            token.PreviewChanged -= UpdatePreview;
        }

        var names = BranchPattern.ParseTokens(Pattern);
        Tokens.Clear();

        foreach (var name in names)
        {
            _remembered.TryGetValue(name, out var code);
            var token = new BranchTokenDefaultViewModel(name, code ?? string.Empty, _evaluator);
            token.PreviewChanged += UpdatePreview;
            Tokens.Add(token);
            _ = token.CheckAsync(immediate: true);
        }
    }

    private void UpdatePreview() =>
        Preview = BranchPattern.Build(
            Pattern,
            Tokens.ToDictionary(t => t.Name, t => t.Preview, StringComparer.OrdinalIgnoreCase));
}
