using System;

namespace NRSGitCheck.Models;

/// <summary>
/// A pull request identified by the user, either as a full link or a bare number.
/// <see cref="Owner"/> and <see cref="Repository"/> are only known for a link, and
/// are used to catch a link pasted from a different project.
/// </summary>
public sealed record PullRequestReference(int Number, string? Owner, string? Repository)
{
    /// <summary>The branch this PR is checked out onto locally.</summary>
    public string LocalBranch => $"pr-{Number}";

    /// <summary>The ref GitHub publishes for a PR's head commit.</summary>
    public string RemoteRef => $"refs/pull/{Number}/head";

    /// <summary>"owner/repo" when the reference came from a link, otherwise null.</summary>
    public string? Slug => Owner is not null && Repository is not null ? $"{Owner}/{Repository}" : null;

    /// <summary>
    /// Whether a branch is one of the local <c>pr-N</c> branches this application
    /// creates when reviewing a pull request. Used to tell "I am reviewing a PR" from
    /// "I am on my own work", which is not otherwise recorded anywhere.
    /// </summary>
    public static bool IsPullRequestBranch(string? branch) => TryGetNumberFromBranch(branch, out _);

    /// <summary>Reads the pull request number back out of a <c>pr-N</c> branch name.</summary>
    public static bool TryGetNumberFromBranch(string? branch, out int number)
    {
        number = 0;

        if (branch is null || !branch.StartsWith("pr-", StringComparison.Ordinal))
            return false;

        var rest = branch[3..];
        return IsAllDigits(rest) && int.TryParse(rest, out number) && number > 0;
    }

    /// <summary>
    /// Accepts a pull request URL (any host, with or without scheme, and with any
    /// trailing path such as /files), or a bare number optionally prefixed with '#'.
    /// </summary>
    public static bool TryParse(string? input, out PullRequestReference? reference, out string? error)
    {
        reference = null;
        error = null;

        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Paste a pull request link or number.";
            return false;
        }

        // Bare "123" or "#123".
        var bare = text.TrimStart('#');
        if (IsAllDigits(bare))
        {
            if (!int.TryParse(bare, out var onlyNumber) || onlyNumber <= 0)
            {
                error = $"'{text}' is not a valid pull request number.";
                return false;
            }

            reference = new PullRequestReference(onlyNumber, null, null);
            return true;
        }

        // Drop scheme, query and fragment, then read the /owner/repo/pull/N shape.
        var path = text;
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            path = path[(scheme + 3)..];

        var cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
            path = path[..cut];

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // parts = [host, owner, repo, "pull", number, ...]
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (!parts[i].Equals("pull", StringComparison.OrdinalIgnoreCase) &&
                !parts[i].Equals("pulls", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!int.TryParse(parts[i + 1], out var number) || number <= 0)
                break;

            // The two segments before "pull" are owner and repo, when present.
            var owner = i >= 2 ? parts[i - 2] : null;
            var repo = i >= 1 ? TrimGitSuffix(parts[i - 1]) : null;

            reference = new PullRequestReference(number, owner, repo);
            return true;
        }

        error = "That doesn't look like a pull request link. " +
                "Paste something like https://github.com/owner/repo/pull/123, or just the number.";
        return false;
    }

    /// <summary>
    /// Pulls "owner/repo" out of a remote URL, handling both the HTTPS and the SSH
    /// (<c>git@host:owner/repo.git</c>) forms. Returns null if it is neither.
    /// </summary>
    public static string? SlugFromRemoteUrl(string? remoteUrl)
    {
        var url = remoteUrl?.Trim();
        if (string.IsNullOrEmpty(url))
            return null;

        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            url = url[(scheme + 3)..];

            // Strip any userinfo ("git@host/..." or "token@host/...").
            var at = url.IndexOf('@');
            if (at >= 0)
                url = url[(at + 1)..];
        }
        else
        {
            // scp-like syntax: git@github.com:owner/repo.git
            var colon = url.IndexOf(':');
            if (colon < 0)
                return null;
            url = url[(colon + 1)..];
        }

        var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        // Last two segments are owner and repo; anything before is host/path.
        var repo = TrimGitSuffix(parts[^1]);
        var owner = parts[^2];
        return repo.Length == 0 || owner.Length == 0 ? null : $"{owner}/{repo}";
    }

    private static string TrimGitSuffix(string name) =>
        name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool IsAllDigits(string text)
    {
        if (text.Length == 0)
            return false;
        foreach (var c in text)
            if (c is < '0' or > '9')
                return false;
        return true;
    }
}
