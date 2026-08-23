using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NRSGitCheck.Services;

/// <summary>
/// Runs the <c>git</c> CLI as a child process. The CLI is used rather than
/// LibGit2Sharp because it picks up the user's existing credential helper
/// (Windows Credential Manager, SSH agent) for network operations, which the
/// library cannot do without this application handling secrets itself.
/// </summary>
public sealed class GitCommandService : IGitCommandService
{
    /// <summary>Ceiling for a single git invocation, so a stalled network call cannot hang the UI.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public async Task<GitCommandResult> PullMainAsync(
        string workingDirectory, string? mainBranch, string? currentBranch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return new GitCommandResult(false, "No repository is open.");
        if (string.IsNullOrWhiteSpace(mainBranch))
            return new GitCommandResult(false, "No main/master branch was found in this repository.");

        // A remote-tracking name ("origin/main") means there is no local main to move;
        // the fetch below is the whole job.
        var slash = mainBranch.IndexOf('/');
        var isRemoteOnly = slash >= 0;
        var remote = isRemoteOnly ? mainBranch[..slash] : "origin";
        var localMain = isRemoteOnly ? mainBranch[(slash + 1)..] : mainBranch;

        var fetch = await RunAsync(workingDirectory, ct, "fetch", "--prune", remote);
        if (!fetch.Success)
            return fetch;

        if (isRemoteOnly)
            return new GitCommandResult(true, $"Fetched {remote}. {mainBranch} is up to date.");

        if (string.Equals(currentBranch, localMain, StringComparison.Ordinal))
        {
            // main is checked out: fast-forward only, so local commits are never rewritten.
            var merge = await RunAsync(workingDirectory, ct, "merge", "--ff-only", $"{remote}/{localMain}");
            return merge.Success
                ? new GitCommandResult(true, $"{localMain} fast-forwarded to {remote}/{localMain}.")
                : merge;
        }

        // main is not checked out: update the ref in place. Git refuses this when it
        // would not be a fast-forward, so a diverged main fails loudly instead of moving.
        var update = await RunAsync(workingDirectory, ct, "fetch", remote, $"{localMain}:{localMain}");
        return update.Success
            ? new GitCommandResult(true, $"{localMain} updated from {remote}/{localMain}.")
            : update;
    }

    private static async Task<GitCommandResult> RunAsync(
        string workingDirectory, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        // Without this, git can block indefinitely waiting on a terminal that isn't there.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return new GitCommandResult(false, "Could not run 'git'. Is Git installed and on your PATH?");
        }
        catch (Exception ex)
        {
            return new GitCommandResult(false, $"Could not run git: {ex.Message}");
        }

        // Drain both pipes concurrently with the wait; a full pipe would deadlock the child.
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
            TryKill(process);
            return new GitCommandResult(false, ct.IsCancellationRequested
                ? "Cancelled."
                : $"git {args[0]} timed out after {CommandTimeout.TotalSeconds:0} seconds.");
        }

        if (process.ExitCode == 0)
            return new GitCommandResult(true, (await stdout).Trim());

        var message = (await stderr).Trim();
        if (message.Length == 0)
            message = (await stdout).Trim();
        if (message.Length == 0)
            message = $"git {args[0]} failed with exit code {process.ExitCode}.";

        return new GitCommandResult(false, FirstLines(message, 3));
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    /// <summary>Git errors can run to several paragraphs; the status bar only has room for the gist.</summary>
    private static string FirstLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= count)
            return text.Replace("\r", "").Replace('\n', ' ').Trim();

        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
            sb.Append(lines[i].Trim()).Append(' ');
        return sb.ToString().Trim() + " …";
    }
}
