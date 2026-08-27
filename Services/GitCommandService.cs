using System;
using System.ComponentModel;
using NRSGitCheck.Models;
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

    public async Task<GitCommandResult> CheckoutPullRequestAsync(
        string workingDirectory, PullRequestReference pr, string? currentBranch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return new GitCommandResult(false, "No repository is open.");

        var branch = pr.LocalBranch;

        // Already sitting on this PR's branch: Git refuses to update a checked-out
        // branch through a refspec, so fast-forward the working tree instead.
        if (string.Equals(currentBranch, branch, StringComparison.Ordinal))
        {
            var refresh = await RunAsync(workingDirectory, ct, "fetch", "origin", pr.RemoteRef);
            if (!refresh.Success)
                return refresh;

            var ff = await RunAsync(workingDirectory, ct, "merge", "--ff-only", "FETCH_HEAD");
            return ff.Success
                ? new GitCommandResult(true, $"Updated {branch} to the latest push on PR #{pr.Number}.")
                : ff;
        }

        // Land the PR head on a local branch. Try fast-forward first (no leading
        // '+'), which covers the ordinary case of new commits being pushed.
        var reset = false;
        var fetch = await RunAsync(
            workingDirectory, ct, "fetch", "origin", $"{pr.RemoteRef}:refs/heads/{branch}");

        if (!fetch.Success)
        {
            // A force-pushed PR (rebased or amended) is not a fast-forward. The pr-N
            // branch exists only to review this PR, so point it at the current head
            // rather than leaving the user stuck -- and say so in the result.
            if (!IsNonFastForward(fetch.Message))
                return fetch;

            var forced = await RunAsync(
                workingDirectory, ct, "fetch", "origin", $"+{pr.RemoteRef}:refs/heads/{branch}");
            if (!forced.Success)
                return forced;

            reset = true;
        }

        var checkout = await RunAsync(workingDirectory, ct, "checkout", branch);
        if (!checkout.Success)
            return checkout;

        return new GitCommandResult(true, reset
            ? $"Checked out PR #{pr.Number}; {branch} was reset to its force-pushed head."
            : $"Checked out PR #{pr.Number} as {branch}.");
    }

    public async Task<GitCommandResult> CheckoutBranchAsync(
        string workingDirectory, string branch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return new GitCommandResult(false, "No repository path.");
        if (string.IsNullOrWhiteSpace(branch))
            return new GitCommandResult(false, "No branch was selected.");

        var checkout = await RunAsync(workingDirectory, ct, "checkout", branch);
        return checkout.Success
            ? new GitCommandResult(true, $"Checked out {branch}.")
            : checkout;
    }

    public async Task<GitCommandResult> CreateBranchAsync(
        string workingDirectory, string branch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return new GitCommandResult(false, "No repository path.");
        if (string.IsNullOrWhiteSpace(branch))
            return new GitCommandResult(false, "Enter a branch name.");

        var create = await RunAsync(workingDirectory, ct, "checkout", "-b", branch);
        return create.Success
            ? new GitCommandResult(true, $"Created and checked out {branch}.")
            : create;
    }

    public async Task<GitCommandResult> PushAsync(
        string workingDirectory, string branch, bool setUpstream, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return new GitCommandResult(false, "No repository path.");

        if (!setUpstream)
        {
            // The branch already tracks something; let Git send it there rather than
            // second-guessing which remote that is.
            var push = await RunAsync(workingDirectory, ct, "push");
            return push.Success
                ? new GitCommandResult(true, $"Pushed {Describe(branch)} to its upstream.")
                : push;
        }

        if (string.IsNullOrWhiteSpace(branch))
            return new GitCommandResult(false, "No branch to publish.");

        var publish = await RunAsync(workingDirectory, ct, "push", "--set-upstream", "origin", branch);
        return publish.Success
            ? new GitCommandResult(true, $"Pushed {branch} to origin and set it as the upstream.")
            : publish;
    }

    private static string Describe(string branch) =>
        string.IsNullOrWhiteSpace(branch) ? "the current branch" : branch;

    public async Task<GitCommandResult> CommitAllAsync(
        string workingDirectory, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return new GitCommandResult(false, "No repository path.");
        if (string.IsNullOrWhiteSpace(message))
            return new GitCommandResult(false, "Enter a commit message.");

        // Stage everything first, so new and deleted files are part of the commit
        // rather than silently left behind in the working tree.
        var stage = await RunAsync(workingDirectory, ct, "add", "-A");
        if (!stage.Success)
            return stage;

        var commit = await RunAsync(workingDirectory, ct, "commit", "-m", message.Trim());
        if (!commit.Success)
            return commit;

        // Git's first line is "[branch 1a2b3c4] subject", which says more than
        // anything this could compose.
        var summary = FirstLines(commit.Message, 1);
        return new GitCommandResult(true, summary.Length > 0 ? summary : "Committed.");
    }

    public async Task<GitCommandResult> DiscardChangesAsync(
        string workingDirectory, bool deleteUntrackedFiles, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return new GitCommandResult(false, "No repository path.");

        var reset = await RunAsync(workingDirectory, ct, "reset", "--hard");
        if (!reset.Success)
            return reset;

        if (!deleteUntrackedFiles)
            return new GitCommandResult(true, "Reverted tracked files to the checked-out commit.");

        // -d walks untracked directories; no -x, so ignored files (build output,
        // local config) survive. Deleting those is never implied by "discard changes".
        var clean = await RunAsync(workingDirectory, ct, "clean", "-fd");
        return clean.Success
            ? new GitCommandResult(true, "Reverted tracked files and deleted untracked ones.")
            : clean;
    }

    /// <summary>Recognizes Git's refusal to move a ref backwards or sideways.</summary>
    private static bool IsNonFastForward(string message) =>
        message.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("not a fast-forward", StringComparison.OrdinalIgnoreCase);

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
