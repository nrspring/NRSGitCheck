using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NRSGitCheck.Models;
using NRSGitCheck.Services;

namespace NRSGitCheck.Tests;

/// <summary>
/// Stands in for the LibGit2Sharp branch reader. Shared by every test that builds a
/// <see cref="NRSGitCheck.ViewModels.RepositoriesViewModel"/>.
/// </summary>
internal sealed class FakeBranchInfo : IBranchInfoService
{
    public List<LocalBranchInfo> Branches { get; } = new();

    /// <summary>Set to make the read throw, as an unreadable repository would.</summary>
    public string? Failure { get; set; }

    public IReadOnlyList<LocalBranchInfo> ReadLocalBranches(string path) =>
        Failure is { } failure ? throw new InvalidOperationException(failure) : Branches;
}

/// <summary>
/// Stands in for the GitHub CLI. Defaults to "not installed", so tests that do not
/// care about pull requests never look as if they reached GitHub.
/// </summary>
internal sealed class FakeGitHubCli : IGitHubCliService
{
    public GitHubCliStatus Status { get; set; } = GitHubCliStatus.NotInstalled;

    public List<PullRequestInfo> PullRequests { get; } = new();

    /// <summary>Set to make <see cref="ListPullRequestsAsync"/> fail with this message.</summary>
    public string? ListError { get; set; }

    public int Checks { get; private set; }

    public Task<GitHubCliStatus> CheckAsync(CancellationToken ct = default)
    {
        Checks++;
        return Task.FromResult(Status);
    }

    public Task<PullRequestListResult> ListPullRequestsAsync(string workingDirectory, CancellationToken ct = default) =>
        Task.FromResult(ListError is { } error
            ? PullRequestListResult.Failed(error)
            : new PullRequestListResult(true, PullRequests, null));
}
