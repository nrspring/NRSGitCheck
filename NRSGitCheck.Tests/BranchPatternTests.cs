using System.Collections.Generic;
using NRSGitCheck.Models;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// The new-branch name pattern: which placeholders it exposes, what it refuses, and
/// how field values are folded into a legal branch name.
/// </summary>
public sealed class BranchPatternTests
{
    private const string Pattern = "nrs/{TodaysDate}-sa-{SANumber}-{description}";

    [Fact]
    public void Tokens_come_back_in_the_order_they_appear()
    {
        Assert.Equal(
            new[] { "TodaysDate", "SANumber", "description" },
            BranchPattern.ParseTokens(Pattern));
    }

    [Fact]
    public void A_token_used_twice_is_only_asked_for_once()
    {
        Assert.Equal(new[] { "Date" }, BranchPattern.ParseTokens("{Date}/rel-{Date}"));
    }

    [Fact]
    public void An_empty_pattern_has_no_tokens_and_no_complaint()
    {
        Assert.Empty(BranchPattern.ParseTokens(""));
        Assert.Null(BranchPattern.Validate(""));
    }

    [Theory]
    [InlineData("nrs/{Date", "never closed")]
    [InlineData("nrs/{}", "no name")]
    [InlineData("nrs/{a{b}}", "inside another")]
    [InlineData("nrs/date}", "without a matching")]
    [InlineData("nrs/fixed-name", "no {placeholders}")]
    public void Malformed_patterns_are_explained(string pattern, string expected)
    {
        var error = BranchPattern.Validate(pattern);

        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Values_are_substituted_into_the_pattern()
    {
        var values = new Dictionary<string, string>
        {
            ["TodaysDate"] = "20260824",
            ["SANumber"] = "1234",
            ["description"] = "add-branch-dialog",
        };

        Assert.Equal("nrs/20260824-sa-1234-add-branch-dialog", BranchPattern.Build(Pattern, values));
    }

    [Fact]
    public void A_field_left_empty_leaves_a_gap_rather_than_braces()
    {
        var values = new Dictionary<string, string>
        {
            ["TodaysDate"] = "20260824",
            ["SANumber"] = "1234",
        };

        Assert.Equal("nrs/20260824-sa-1234-", BranchPattern.Build(Pattern, values));
    }

    [Theory]
    [InlineData("add branch dialog", "add-branch-dialog")]
    [InlineData("  padded  out  ", "padded-out")]
    [InlineData("we:re*not^allowed", "werenotallowed")]
    [InlineData("", "")]
    public void Values_are_made_safe_for_a_ref_name(string value, string expected)
    {
        Assert.Equal(expected, BranchPattern.SanitizeValue(value));
    }

    [Fact]
    public void Sanitizing_happens_while_building_too()
    {
        var values = new Dictionary<string, string>
        {
            ["TodaysDate"] = "20260824",
            ["SANumber"] = "12 34",
            ["description"] = "fix the thing",
        };

        Assert.Equal("nrs/20260824-sa-12-34-fix-the-thing", BranchPattern.Build(Pattern, values));
    }
}
