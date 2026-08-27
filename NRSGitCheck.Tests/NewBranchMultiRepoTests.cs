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
/// Creating the same branch across several repositories at once from the
/// Repositories tab: checking rows, opening the dialog for the selection, and
/// what happens when some of them refuse the branch.
/// </summary>
public sealed class NewBranchMultiRepoTests
{
    [Fact]
    public void Checking_a_row_updates_the_selection_count()
    {
        var repositories = Build("repo-a", "repo-b");

        Assert.False(repositories.HasSelection);
        Assert.False(repositories.NewBranchForSelectedCommand.CanExecute(null));

        repositories.Repositories[0].IsSelected = true;

        Assert.Equal(1, repositories.SelectedCount);
        Assert.True(repositories.HasSelection);
        Assert.True(repositories.NewBranchForSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void Opening_for_the_selection_targets_every_checked_repository()
    {
        var repositories = Build("repo-a", "repo-b", "repo-c");
        repositories.Repositories[0].IsSelected = true;
        repositories.Repositories[2].IsSelected = true;

        repositories.NewBranchForSelectedCommand.Execute(null);

        Assert.True(repositories.NewBranch.IsVisible);
        Assert.True(repositories.NewBranch.HasMultipleTargets);
        Assert.Equal(
            new[] { "repo-a", "repo-c" },
            repositories.NewBranch.Targets.Select(t => t.Name));
    }

    [Fact]
    public async Task Creating_succeeds_in_every_selected_repository()
    {
        var commands = new RecordingGitCommands();
        var repositories = Build(commands, "repo-a", "repo-b");
        repositories.Repositories[0].IsSelected = true;
        repositories.Repositories[1].IsSelected = true;

        repositories.NewBranchForSelectedCommand.Execute(null);
        repositories.NewBranch.BranchName = "feature/shared";
        await repositories.NewBranch.CreateCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { ("repo-a", "feature/shared"), ("repo-b", "feature/shared") },
            commands.Created);
        Assert.False(repositories.NewBranch.IsVisible);
        Assert.Null(repositories.ErrorMessage);
        Assert.Contains("2", repositories.Status);
    }

    [Fact]
    public async Task Success_clears_the_checkbox_on_repositories_that_got_the_branch()
    {
        var commands = new RecordingGitCommands();
        var repositories = Build(commands, "repo-a", "repo-b");
        repositories.Repositories[0].IsSelected = true;
        repositories.Repositories[1].IsSelected = true;

        repositories.NewBranchForSelectedCommand.Execute(null);
        repositories.NewBranch.BranchName = "feature/shared";
        await repositories.NewBranch.CreateCommand.ExecuteAsync(null);

        Assert.False(repositories.Repositories[0].IsSelected);
        Assert.False(repositories.Repositories[1].IsSelected);
    }

    [Fact]
    public async Task A_repository_that_refuses_the_branch_leaves_the_dialog_open_on_just_that_one()
    {
        var commands = new RecordingGitCommands { RefusePaths = { "repo-b" } };
        var repositories = Build(commands, "repo-a", "repo-b");
        repositories.Repositories[0].IsSelected = true;
        repositories.Repositories[1].IsSelected = true;

        repositories.NewBranchForSelectedCommand.Execute(null);
        repositories.NewBranch.BranchName = "feature/shared";
        await repositories.NewBranch.CreateCommand.ExecuteAsync(null);

        // The one that worked is done and reported; the dialog stays open scoped
        // to the one that still needs the branch.
        Assert.True(repositories.NewBranch.IsVisible);
        Assert.False(repositories.NewBranch.HasMultipleTargets);
        Assert.Equal("repo-b", Assert.Single(repositories.NewBranch.Targets).Name);
        Assert.False(repositories.Repositories[0].IsSelected);
        Assert.True(repositories.Repositories[1].IsSelected);
        Assert.NotNull(repositories.NewBranch.Error);
        Assert.NotNull(repositories.ErrorMessage);
    }

    [Fact]
    public async Task Retrying_after_a_partial_failure_only_touches_the_repository_still_needing_it()
    {
        var commands = new RecordingGitCommands { RefusePaths = { "repo-b" } };
        var repositories = Build(commands, "repo-a", "repo-b");
        repositories.Repositories[0].IsSelected = true;
        repositories.Repositories[1].IsSelected = true;

        repositories.NewBranchForSelectedCommand.Execute(null);
        repositories.NewBranch.BranchName = "feature/shared";
        await repositories.NewBranch.CreateCommand.ExecuteAsync(null);

        commands.RefusePaths.Clear();
        await repositories.NewBranch.CreateCommand.ExecuteAsync(null);

        Assert.Equal(
            new[] { ("repo-a", "feature/shared"), ("repo-b", "feature/shared") },
            commands.Created);
        Assert.False(repositories.NewBranch.IsVisible);
    }

    [Fact]
    public void The_row_button_still_targets_only_its_own_repository()
    {
        var repositories = Build("repo-a", "repo-b");
        repositories.Repositories[1].IsSelected = true; // a different repo is checked

        repositories.Repositories[0].NewBranchCommand.Execute(null);

        Assert.False(repositories.NewBranch.HasMultipleTargets);
        Assert.Equal("repo-a", repositories.NewBranch.RepositoryName);
    }

    // --- harness --------------------------------------------------------------

    private static RepositoriesViewModel Build(params string[] names) =>
        Build(new RecordingGitCommands(), names);

    private static RepositoriesViewModel Build(RecordingGitCommands commands, params string[] names)
    {
        var settings = new StubSettings();
        foreach (var name in names)
            settings.Settings.TrackedRepositories.Add(new TrackedRepository { Path = name, Name = name });

        var repositories = new RepositoriesViewModel(
            settings,
            new StubRepositoryStatus(),
            commands,
            new StubFolderPicker(),
            new RoslynExpressionEvaluator(),
            new StubClipboard(),
            new RecordingEditorService());

        foreach (var row in repositories.Repositories)
            row.Apply(ValidStatus(row.Path));

        return repositories;
    }

    private static RepositoryStatus ValidStatus(string path) => new(
        path, path, IsValid: true, Error: null, CurrentBranch: "main",
        IsDetachedHead: false, IsHeadUnborn: false, LocalBranches: new[] { "main" },
        MainBranch: "main", UncommittedCount: 0, HasUpstream: true, AheadBy: 0, BehindBy: 0,
        HasRemote: true, Changes: Array.Empty<WorkingTreeChange>(), UntrackedCount: 0);

    private sealed class RecordingGitCommands : IGitCommandService
    {
        public List<(string Path, string Branch)> Created { get; } = new();
        public HashSet<string> RefusePaths { get; } = new();

        public Task<GitCommandResult> CreateBranchAsync(
            string workingDirectory, string branch, CancellationToken ct = default)
        {
            if (RefusePaths.Contains(workingDirectory))
                return Task.FromResult(new GitCommandResult(false, $"a branch named '{branch}' already exists"));

            Created.Add((workingDirectory, branch));
            return Task.FromResult(new GitCommandResult(true, $"created {branch}"));
        }

        public Task<GitCommandResult> CommitAllAsync(
            string workingDirectory, string message, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"committed {message}"));

        public Task<GitCommandResult> PushAsync(
            string workingDirectory, string branch, bool setUpstream, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"pushed {branch}"));

        public Task<GitCommandResult> DiscardChangesAsync(
            string workingDirectory, bool deleteUntrackedFiles, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "discarded"));

        public Task<GitCommandResult> PullMainAsync(
            string workingDirectory, string? mainBranch, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "up to date"));

        public Task<GitCommandResult> CheckoutPullRequestAsync(
            string workingDirectory, PullRequestReference pr, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"checked out pr-{pr.Number}"));

        public Task<GitCommandResult> CheckoutBranchAsync(
            string workingDirectory, string branch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"checked out {branch}"));
    }

    private sealed class StubRepositoryStatus : IRepositoryStatusService
    {
        public RepositoryStatus Read(string path) => ValidStatus(path);

        public Task<IReadOnlyList<RepositoryStatus>> ReadAllAsync(
            IEnumerable<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepositoryStatus>>(paths.Select(Read).ToList());
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

    private sealed class StubClipboard : IClipboardService
    {
        public Task<bool> SetTextAsync(string? text) => Task.FromResult(true);
    }
}
