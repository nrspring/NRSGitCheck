using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace NRSGitCheck.Services;

/// <summary>
/// Roslyn-scripting implementation of <see cref="IExpressionEvaluator"/>. Compiled
/// scripts are cached by source text, because the same handful of expressions are
/// re-run every time the create-branch dialog opens and compilation is the slow part.
/// </summary>
public sealed class RoslynExpressionEvaluator : IExpressionEvaluator
{
    /// <summary>
    /// Ceiling for one expression. A field default is meant to be a one-liner; this
    /// stops a stray loop from hanging the dialog. It bounds the wait, not the code —
    /// a runaway script keeps running on its own thread until the process exits.
    /// </summary>
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(3);

    /// <summary>What the expressions can reach without qualifying names themselves.</summary>
    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithImports("System", "System.Linq", "System.Text", "System.Globalization")
        .WithReferences(
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(CultureInfo).Assembly);

    private readonly ConcurrentDictionary<string, Script<object>> _scripts = new(StringComparer.Ordinal);

    public string? Validate(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        try
        {
            var diagnostics = GetScript(code).Compile();
            return Describe(diagnostics);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<ExpressionResult> EvaluateAsync(string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return ExpressionResult.Ok(string.Empty);

        Script<object> script;
        try
        {
            script = GetScript(code);
            if (Describe(script.Compile()) is { } compileError)
                return ExpressionResult.Failed(compileError);
        }
        catch (Exception ex)
        {
            return ExpressionResult.Failed(ex.Message);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(EvaluationTimeout);

        try
        {
            var run = script.RunAsync(cancellationToken: timeout.Token);

            // RunAsync only observes cancellation between statements, so bound the
            // wait as well: the dialog stays usable even if the script does not stop.
            var finished = await Task.WhenAny(run, Task.Delay(EvaluationTimeout, timeout.Token));
            if (finished != run)
                return ExpressionResult.Failed($"The expression did not finish within {EvaluationTimeout.TotalSeconds:0} seconds.");

            var state = await run;
            return ExpressionResult.Ok(state.ReturnValue?.ToString() ?? string.Empty);
        }
        catch (CompilationErrorException ex)
        {
            return ExpressionResult.Failed(ex.Diagnostics.FirstOrDefault()?.GetMessage() ?? ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ExpressionResult.Failed("Cancelled.");
        }
        catch (OperationCanceledException)
        {
            return ExpressionResult.Failed($"The expression did not finish within {EvaluationTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            // The user's own code threw: report it the way a formula field would.
            return ExpressionResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private Script<object> GetScript(string code) =>
        _scripts.GetOrAdd(code, c => CSharpScript.Create<object>(c, Options));

    /// <summary>The first compiler error, positioned, or null when there are none.</summary>
    private static string? Describe(IEnumerable<Diagnostic> diagnostics)
    {
        var error = diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error is null)
            return null;

        var column = error.Location.GetLineSpan().StartLinePosition.Character + 1;
        return $"Column {column}: {error.GetMessage()}";
    }
}
