using System;
using System.Threading.Tasks;
using NRSGitCheck.Services;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// The user-authored C# expressions behind branch-name fields: what compiles, what
/// they produce, and how badly-written ones are reported rather than thrown.
/// </summary>
public sealed class ExpressionEvaluatorTests
{
    private readonly RoslynExpressionEvaluator _evaluator = new();

    [Fact]
    public void A_well_formed_expression_validates()
    {
        Assert.Null(_evaluator.Validate("System.DateTime.Now.ToString(\"yyyyMMdd\")"));
    }

    [Fact]
    public void System_is_imported_so_short_names_work()
    {
        Assert.Null(_evaluator.Validate("DateTime.Now.ToString(\"yyyy-MM-dd\")"));
    }

    [Fact]
    public void An_empty_expression_is_valid_and_produces_nothing()
    {
        Assert.Null(_evaluator.Validate(null));
        Assert.Null(_evaluator.Validate("   "));
    }

    [Fact]
    public void A_syntax_error_is_reported_with_its_position()
    {
        var error = _evaluator.Validate("DateTime.Now.ToStr(");

        Assert.NotNull(error);
        Assert.Contains("Column", error);
    }

    [Fact]
    public void An_unknown_member_is_reported()
    {
        var error = _evaluator.Validate("DateTime.Now.Nonsense()");

        Assert.NotNull(error);
        Assert.Contains("Nonsense", error);
    }

    [Fact]
    public async Task Evaluating_returns_the_expression_value()
    {
        var result = await _evaluator.EvaluateAsync("\"abc\" + 123");

        Assert.True(result.Success, result.Error);
        Assert.Equal("abc123", result.Value);
    }

    [Fact]
    public async Task A_date_expression_returns_todays_date()
    {
        var result = await _evaluator.EvaluateAsync("DateTime.Now.ToString(\"yyyyMMdd\")");

        Assert.True(result.Success, result.Error);
        Assert.Equal(DateTime.Now.ToString("yyyyMMdd"), result.Value);
    }

    [Fact]
    public async Task A_non_string_result_is_converted()
    {
        var result = await _evaluator.EvaluateAsync("40 + 2");

        Assert.True(result.Success, result.Error);
        Assert.Equal("42", result.Value);
    }

    [Fact]
    public async Task An_empty_expression_evaluates_to_an_empty_value()
    {
        var result = await _evaluator.EvaluateAsync("");

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.Value);
    }

    [Fact]
    public async Task Code_that_does_not_compile_comes_back_as_a_failure()
    {
        var result = await _evaluator.EvaluateAsync("this is not C#");

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task An_exception_thrown_by_the_expression_is_reported()
    {
        var result = await _evaluator.EvaluateAsync("int.Parse(\"not a number\")");

        Assert.False(result.Success);
        Assert.Contains("FormatException", result.Error);
    }
}
