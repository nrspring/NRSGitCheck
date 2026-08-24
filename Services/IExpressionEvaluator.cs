using System.Threading;
using System.Threading.Tasks;

namespace NRSGitCheck.Services;

/// <summary>The outcome of running one user-authored expression.</summary>
public sealed record ExpressionResult(bool Success, string Value, string? Error)
{
    public static ExpressionResult Ok(string value) => new(true, value, null);
    public static ExpressionResult Failed(string error) => new(false, string.Empty, error);
}

/// <summary>
/// Compiles and runs the small C# expressions the user writes to seed branch-name
/// fields (for example <c>DateTime.Now.ToString("yyyyMMdd")</c>). The code is the
/// user's own, entered in this application's settings, and runs in-process with no
/// sandbox — it is a formula field, not a plugin host.
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>
    /// Compiles the expression and returns the first compiler error, or null when it
    /// compiles (an empty expression is valid and simply produces no value).
    /// </summary>
    string? Validate(string? code);

    /// <summary>
    /// Runs the expression and converts its result to a string. Compiler errors,
    /// exceptions thrown by the code, and expressions that run too long all come back
    /// as a failed result rather than an exception.
    /// </summary>
    Task<ExpressionResult> EvaluateAsync(string? code, CancellationToken ct = default);
}
