using System;
using System.Collections.Generic;
using System.Text;

namespace NRSGitCheck.Models;

/// <summary>
/// The new-branch name pattern: literal text with <c>{Token}</c> placeholders, as in
/// <c>nrs/{Date}-sa-{SANumber}-{description}</c>. Each token becomes a field in the
/// create-branch dialog, seeded from the user's default expression for it.
/// </summary>
public static class BranchPattern
{
    /// <summary>Characters Git rejects in a ref name, plus the ones that make branch names awkward.</summary>
    private const string Forbidden = " ~^:?*[]\\\"'<>|";

    /// <summary>
    /// The token names in the pattern, in the order they appear, without duplicates.
    /// A token is <c>{Name}</c>; an empty <c>{}</c> or an unclosed brace yields nothing
    /// here and is reported by <see cref="Validate"/>.
    /// </summary>
    public static IReadOnlyList<string> ParseTokens(string? pattern)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(pattern))
            return tokens;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < pattern.Length)
        {
            var open = pattern.IndexOf('{', i);
            if (open < 0)
                break;

            var close = pattern.IndexOf('}', open + 1);
            if (close < 0)
                break;

            var name = pattern[(open + 1)..close].Trim();
            if (name.Length > 0 && seen.Add(name))
                tokens.Add(name);

            i = close + 1;
        }

        return tokens;
    }

    /// <summary>
    /// Explains what is wrong with the pattern, or null when it is usable. An empty
    /// pattern is allowed — it just means the dialog asks for a plain branch name.
    /// </summary>
    public static string? Validate(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null;

        var depth = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '{')
            {
                if (depth > 0)
                    return "A '{' is opened inside another '{'.";
                depth++;

                var close = pattern.IndexOf('}', i + 1);
                if (close < 0)
                    return "A '{' is never closed.";
                if (pattern[(i + 1)..close].Trim().Length == 0)
                    return "A placeholder has no name — write {Something} between the braces.";
            }
            else if (pattern[i] == '}')
            {
                if (depth == 0)
                    return "A '}' appears without a matching '{'.";
                depth--;
            }
        }

        return ParseTokens(pattern).Count == 0
            ? "The pattern has no {placeholders}, so there is nothing to fill in."
            : null;
    }

    /// <summary>
    /// Substitutes the supplied values into the pattern. Tokens with no value are
    /// replaced with nothing, so a skipped field leaves a gap rather than literal
    /// braces in the branch name.
    /// </summary>
    public static string Build(string? pattern, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(pattern))
            return string.Empty;

        var result = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var open = pattern.IndexOf('{', i);
            if (open < 0)
            {
                result.Append(pattern[i..]);
                break;
            }

            var close = pattern.IndexOf('}', open + 1);
            if (close < 0)
            {
                result.Append(pattern[i..]);
                break;
            }

            result.Append(pattern[i..open]);

            var name = pattern[(open + 1)..close].Trim();
            if (name.Length > 0 && values.TryGetValue(name, out var value))
                result.Append(SanitizeValue(value));

            i = close + 1;
        }

        return result.ToString();
    }

    /// <summary>
    /// Makes one field's value safe to drop into a branch name: trims it, turns runs
    /// of whitespace into a single dash, and drops characters Git will not accept.
    /// </summary>
    public static string SanitizeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = new StringBuilder(value.Length);
        var pendingDash = false;

        foreach (var c in value.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingDash = result.Length > 0;
                continue;
            }

            if (Forbidden.Contains(c) || char.IsControl(c))
                continue;

            if (pendingDash)
            {
                result.Append('-');
                pendingDash = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }
}
