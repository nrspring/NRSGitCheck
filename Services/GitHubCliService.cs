using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Runs the GitHub CLI as a child process. The executable is located on every
/// check rather than once, and <c>PATH</c> is read from the machine and user
/// settings as well as this process: someone who installs gh while the app is open
/// can press "Check again" instead of restarting it.
/// </summary>
public sealed class GitHubCliService : IGitHubCliService
{
    /// <summary>
    /// How many pull requests to read. A branch whose PR is older than this shows as
    /// having none, which is the right answer for anything but long-forgotten branches.
    /// </summary>
    internal const int PullRequestLimit = 200;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _environment;

    public GitHubCliService()
        : this(File.Exists, ReadEnvironment)
    {
    }

    /// <summary>Test seam: the filesystem and environment are the only machine state used.</summary>
    internal GitHubCliService(Func<string, bool> fileExists, Func<string, string?> environment)
    {
        _fileExists = fileExists;
        _environment = environment;
    }

    public async Task<GitHubCliStatus> CheckAsync(CancellationToken ct = default)
    {
        if (Locate(_fileExists, _environment) is not { } gh)
            return GitHubCliStatus.NotInstalled;

        var version = await RunAsync(gh, null, ct, "--version");
        if (version.LaunchFailed)
            return GitHubCliStatus.NotInstalled;

        // Exits non-zero when there is no login, or the stored token no longer works.
        var auth = await RunAsync(gh, null, ct, "auth", "status");
        return auth.ExitCode == 0 ? GitHubCliStatus.Available : GitHubCliStatus.NotSignedIn;
    }

    public async Task<PullRequestListResult> ListPullRequestsAsync(
        string workingDirectory, CancellationToken ct = default)
    {
        if (Locate(_fileExists, _environment) is not { } gh)
            return PullRequestListResult.Failed("The GitHub CLI (gh) was not found.");

        var run = await RunAsync(
            gh, workingDirectory, ct,
            "pr", "list", "--state", "all", "--limit", PullRequestLimit.ToString(),
            "--json", "number,title,state,isDraft,headRefName,isCrossRepository,url");

        if (run.LaunchFailed || run.ExitCode != 0)
            return PullRequestListResult.Failed(FirstLine(run.Error) ?? "gh pr list failed.");

        try
        {
            return new PullRequestListResult(true, ParsePullRequests(run.Output), null);
        }
        catch (JsonException ex)
        {
            return PullRequestListResult.Failed($"Could not read gh's output: {ex.Message}");
        }
    }

    /// <summary>Reads the JSON array <c>gh pr list --json …</c> prints.</summary>
    internal static IReadOnlyList<PullRequestInfo> ParsePullRequests(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<PullRequestInfo>();

        foreach (var pr in doc.RootElement.EnumerateArray())
        {
            var state = pr.GetProperty("state").GetString() switch
            {
                "MERGED" => PullRequestState.Merged,
                "CLOSED" => PullRequestState.Closed,
                _ => PullRequestState.Open,
            };

            list.Add(new PullRequestInfo(
                pr.GetProperty("number").GetInt32(),
                pr.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                state,
                pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
                pr.GetProperty("headRefName").GetString() ?? string.Empty,
                pr.TryGetProperty("isCrossRepository", out var fork) && fork.ValueKind == JsonValueKind.True,
                pr.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty));
        }

        return list;
    }

    // --- Locating gh --------------------------------------------------------

    /// <summary>The gh executable, or null. Install locations first, then every PATH entry.</summary>
    internal static string? Locate(Func<string, bool> fileExists, Func<string, string?> environment)
    {
        var exe = OperatingSystem.IsWindows() ? "gh.exe" : "gh";

        foreach (var root in new[] { environment("ProgramFiles"), environment("ProgramFiles(x86)"),
                     environment("LOCALAPPDATA") is { Length: > 0 } local ? Path.Combine(local, "Programs") : null })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var candidate = Path.Combine(root, "GitHub CLI", exe);
            if (fileExists(candidate))
                return candidate;
        }

        if (environment("PATH") is not { Length: > 0 } path)
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;

            string candidate;
            try
            {
                candidate = Path.Combine(trimmed, exe);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (fileExists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The environment, except that <c>PATH</c> also includes the machine and user
    /// values as they are now — this process's copy was frozen when it started.
    /// </summary>
    private static string? ReadEnvironment(string name)
    {
        if (!string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable(name);

        var parts = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
        };

        return string.Join(Path.PathSeparator, parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    // --- Running gh ---------------------------------------------------------

    private sealed record RunResult(bool LaunchFailed, int ExitCode, string Output, string Error);

    private static async Task<RunResult> RunAsync(
        string executable, string? workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        // Never wait on a prompt nobody can see, and keep update nags out of stderr.
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new RunResult(true, -1, string.Empty, ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new RunResult(false, -1, string.Empty,
                ct.IsCancellationRequested ? "Cancelled." : $"gh {args[0]} timed out.");
        }

        return new RunResult(false, process.ExitCode, await stdout, await stderr);
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
}
