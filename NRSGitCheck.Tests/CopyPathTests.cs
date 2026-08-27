using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using NRSGitCheck.ViewModels;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Copying a tracked repository's path from the Repositories tab.
/// </summary>
public sealed class CopyPathTests
{
    [Fact]
    public async Task Copying_puts_the_repository_path_on_the_clipboard()
    {
        var clipboard = new StubClipboard();
        var row = FirstRow(clipboard, @"C:\work\my-repo");

        await row.CopyPathCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\work\my-repo", clipboard.Text);
    }

    [Fact]
    public async Task A_successful_copy_is_reported_in_the_status_line()
    {
        var clipboard = new StubClipboard();
        var repositories = Build(clipboard, @"C:\work\my-repo");

        await repositories.Repositories[0].CopyPathCommand.ExecuteAsync(null);

        Assert.Contains(@"C:\work\my-repo", repositories.Status);
        Assert.Null(repositories.ErrorMessage);
    }

    [Fact]
    public async Task A_clipboard_that_refuses_is_reported_rather_than_silent()
    {
        var clipboard = new StubClipboard { Succeeds = false };
        var repositories = Build(clipboard, @"C:\work\my-repo");

        await repositories.Repositories[0].CopyPathCommand.ExecuteAsync(null);

        Assert.False(string.IsNullOrEmpty(repositories.ErrorMessage));
    }

    // --- harness ------------------------------------------------------------

    private static TrackedRepositoryViewModel FirstRow(StubClipboard clipboard, string path) =>
        Build(clipboard, path).Repositories[0];

    private static RepositoriesViewModel Build(StubClipboard clipboard, string path)
    {
        var settings = new StubSettings();
        settings.Settings.TrackedRepositories.Add(new TrackedRepository { Path = path, Name = "my-repo" });

        return new RepositoriesViewModel(
            settings,
            new StubRepositoryStatus(),
            new StubGitCommands(),
            new StubFolderPicker(),
            new RoslynExpressionEvaluator(),
            clipboard);
    }

    private sealed class StubClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public bool Succeeds { get; init; } = true;

        public Task<bool> SetTextAsync(string? text)
        {
            if (!Succeeds)
                return Task.FromResult(false);

            Text = text;
            return Task.FromResult(true);
        }
    }

    private sealed class StubRepositoryStatus : IRepositoryStatusService
    {
        public NRSGitCheck.Models.RepositoryStatus Read(string path) =>
            NRSGitCheck.Models.RepositoryStatus.Failed(path, "my-repo", "not read");

        public Task<IReadOnlyList<NRSGitCheck.Models.RepositoryStatus>> ReadAllAsync(
            IEnumerable<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NRSGitCheck.Models.RepositoryStatus>>(paths.Select(Read).ToList());
    }

    private sealed class StubGitCommands : IGitCommandService
    {
        public Task<GitCommandResult> PullMainAsync(
            string workingDirectory, string? mainBranch, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "up to date"));

        public Task<GitCommandResult> CheckoutPullRequestAsync(
            string workingDirectory, PullRequestReference pr, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"checked out pr-{pr.Number}"));

        public Task<GitCommandResult> CheckoutBranchAsync(
            string workingDirectory, string branch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"checked out {branch}"));

        public Task<GitCommandResult> CreateBranchAsync(
            string workingDirectory, string branch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"created {branch}"));

        public Task<GitCommandResult> CommitAllAsync(
            string workingDirectory, string message, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"committed {message}"));

        public Task<GitCommandResult> DiscardChangesAsync(
            string workingDirectory, bool deleteUntrackedFiles, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "discarded"));
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public void Load() { }
        public void Save() { }
        public void AddRecentRepository(string repositoryPath) { }
        public void RemoveRecentRepository(string repositoryPath) { }
        public bool AddTrackedRepository(string repositoryPath) => false;
        public void RemoveTrackedRepository(string repositoryPath) { }
    }

    private sealed class StubFolderPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }
}
