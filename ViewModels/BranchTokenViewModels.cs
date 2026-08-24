using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using NRSGitCheck.Services;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// One field in the create-branch dialog: a token from the pattern, seeded from its
/// default expression and editable before the branch is created.
/// </summary>
public partial class BranchTokenInputViewModel : ViewModelBase
{
    public BranchTokenInputViewModel(string name, string value, string? seedError)
    {
        Name = name;
        _value = value;
        _seedError = seedError;
    }

    /// <summary>The token name as written between the braces in the pattern.</summary>
    public string Name { get; }

    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value) => ValueChanged?.Invoke();

    /// <summary>Raised on every edit so the dialog can refresh the assembled name.</summary>
    public event Action? ValueChanged;

    /// <summary>Why this field started empty, when its default expression failed.</summary>
    [ObservableProperty]
    private string? _seedError;

    public bool HasSeedError => !string.IsNullOrEmpty(SeedError);

    partial void OnSeedErrorChanged(string? value) => OnPropertyChanged(nameof(HasSeedError));
}

/// <summary>
/// One token's default in the pattern settings: the C# expression that seeds it, with
/// live compiler feedback and a preview of what it currently produces.
/// </summary>
public partial class BranchTokenDefaultViewModel : ViewModelBase
{
    private readonly IExpressionEvaluator _evaluator;

    /// <summary>Cancels the pending check when another keystroke arrives.</summary>
    private CancellationTokenSource? _pending;

    public BranchTokenDefaultViewModel(string name, string code, IExpressionEvaluator evaluator)
    {
        Name = name;
        _evaluator = evaluator;
        _code = code;
    }

    /// <summary>The token name as written between the braces in the pattern.</summary>
    public string Name { get; }

    /// <summary>The user's C# expression, for example <c>DateTime.Now.ToString("yyyyMMdd")</c>.</summary>
    [ObservableProperty]
    private string _code;

    partial void OnCodeChanged(string value) => _ = CheckAsync();

    /// <summary>The compiler error, or the exception the expression threw. Null when it is fine.</summary>
    [ObservableProperty]
    private string? _error;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>What the expression produces right now.</summary>
    [ObservableProperty]
    private string _preview = string.Empty;

    partial void OnPreviewChanged(string value)
    {
        OnPropertyChanged(nameof(HasPreview));
        PreviewChanged?.Invoke();
    }

    public bool HasPreview => !string.IsNullOrEmpty(Preview);

    /// <summary>Raised when the preview value changes, so the dialog can rebuild its example name.</summary>
    public event Action? PreviewChanged;

    /// <summary>
    /// Compiles and runs the expression, after a short pause so a check does not run
    /// on every keystroke. Errors are shown rather than thrown.
    /// </summary>
    public async Task CheckAsync(bool immediate = false)
    {
        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        try
        {
            if (!immediate)
                await Task.Delay(350, cts.Token);

            var result = await _evaluator.EvaluateAsync(Code, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            Error = result.Success ? null : result.Error;
            Preview = result.Value;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke.
        }
    }
}
