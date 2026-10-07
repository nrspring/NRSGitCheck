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
/// The Repositories tab's bulk "switch all to main": it moves every repository that
/// is on another branch back to main, but only when doing so cannot touch
/// uncommitted work.
/// </summary>
public sealed class SwitchAllToMainTests
{
    [Fact]
    public async Task Clean_repositories_on_a_feature_branch_are_switched_to_main()
    {
        var f = new Fixture(
            Status("a", "feature"),
            Status("b", "other"));

        Assert.True(f.Owner.SwitchAllToMainCommand.CanExecute(null));
        await f.Owner.SwitchAllToMainCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("a", "main"), ("b", "main") }, f.Git.CheckedOut);
        Assert.Contains("Switched 2 of 2", f.Owner.Status);
        Assert.Null(f.Owner.ErrorMessage);
    }

    [Fact]
    public async Task Repositories_with_uncommitted_changes_are_left_alone()
    {
        var f = new Fixture(
            Status("clean", "feature"),
            Status("dirty", "feature", uncommitted: 3));

        await f.Owner.SwitchAllToMainCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("clean", "main") }, f.Git.CheckedOut);
        Assert.Contains("Skipped 1 with uncommitted changes", f.Owner.Status);
    }

    [Fact]
    public async Task Repositories_already_on_main_are_not_touched()
    {
        var f = new Fixture(Status("a", "main"), Status("b", "feature"));

        await f.Owner.SwitchAllToMainCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("b", "main") }, f.Git.CheckedOut);
    }

    [Fact]
    public void A_repository_with_no_local_main_is_skipped()
    {
        // Only origin/main is known, and it is not among the local branches.
        var f = new Fixture(Status("a", "feature", localBranches: new[] { "feature" }, mainBranch: "origin/main"));

        Assert.False(f.Owner.SwitchAllToMainCommand.CanExecute(null));
        Assert.Empty(f.Git.CheckedOut);
    }

    [Fact]
    public async Task A_remote_only_main_name_is_checked_out_by_its_local_name()
    {
        var f = new Fixture(Status(
            "a", "feature", localBranches: new[] { "feature", "master" }, mainBranch: "origin/master"));

        await f.Owner.SwitchAllToMainCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("a", "master") }, f.Git.CheckedOut);
    }

    [Fact]
    public void Nothing_to_do_disables_the_command()
    {
        var allOnMain = new Fixture(Status("a", "main"), Status("b", "main"));
        Assert.False(allOnMain.Owner.SwitchAllToMainCommand.CanExecute(null));

        var allDirty = new Fixture(Status("a", "feature", uncommitted: 1));
        Assert.False(allDirty.Owner.SwitchAllToMainCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_refused_checkout_is_reported_and_the_rest_still_run()
    {
        var f = new Fixture(Status("a", "feature"), Status("b", "feature"));
        f.Git.Refuse = "a";

        await f.Owner.SwitchAllToMainCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("b", "main") }, f.Git.CheckedOut);
        Assert.Contains("Switched 1 of 2; 1 failed", f.Owner.Status);
        Assert.Contains("a:", f.Owner.ErrorMessage);
    }

    [Fact]
    public async Task Once_switched_the_command_has_nothing_left_to_do()
    {
        var f = new Fixture(Status("a", "feature"));
        f.Statuses.AfterCheckout = Status("a", "main");

        await f.Owner.SwitchAllToMainCommand.ExecuteAsync(null);

        Assert.False(f.Owner.SwitchAllToMainCommand.CanExecute(null));
    }

    // --- Test doubles -------------------------------------------------------

    private static RepositoryStatus Status(
        string name, string branch, int uncommitted = 0,
        string[]? localBranches = null, string mainBranch = "main") => new(
        Path: name, Name: name, IsValid: true, Error: null, CurrentBranch: branch,
        IsDetachedHead: false, IsHeadUnborn: false,
        LocalBranches: localBranches ?? new[] { branch, "main" }.Distinct().ToArray(),
        MainBranch: mainBranch, UncommittedCount: uncommitted, HasUpstream: true, AheadBy: 0, BehindBy: 0,
        HasRemote: true, Changes: Array.Empty<WorkingTreeChange>(), UntrackedCount: 0);

    private sealed class RecordingGitCommands : IGitCommandService
    {

        public System.Threading.Tasks.Task<GitCommandResult> DeleteBranchAsync(

            string workingDirectory, string branch, bool force, System.Threading.CancellationToken ct = default) =>

            System.Threading.Tasks.Task.FromResult(new GitCommandResult(true, $"Deleted branch {branch}."));

        public List<(string Path, string Branch)> CheckedOut { get; } = new();
        public string? Refuse { get; set; }

        public Task<GitCommandResult> CheckoutBranchAsync(
            string workingDirectory, string branch, CancellationToken ct = default)
        {
            if (workingDirectory == Refuse)
                return Task.FromResult(new GitCommandResult(false, "your local changes would be overwritten"));

            CheckedOut.Add((workingDirectory, branch));
            return Task.FromResult(new GitCommandResult(true, $"checked out {branch}"));
        }

        public Task<GitCommandResult> PullMainAsync(
            string workingDirectory, string? mainBranch, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "up to date"));

        public Task<GitCommandResult> CheckoutPullRequestAsync(
            string workingDirectory, PullRequestReference pr, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "checked out"));

        public Task<GitCommandResult> CreateBranchAsync(
            string workingDirectory, string branch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"created {branch}"));

        public Task<GitCommandResult> CommitAllAsync(
            string workingDirectory, string message, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "committed"));

        public Task<GitCommandResult> PushAsync(
            string workingDirectory, string branch, bool setUpstream, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"pushed {branch}"));

        public Task<GitCommandResult> DiscardChangesAsync(
            string workingDirectory, bool deleteUntrackedFiles, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "discarded"));
    }

    /// <summary>Serves back whatever status a row was last given, unless a checkout has changed it.</summary>
    private sealed class StubStatusService : IRepositoryStatusService
    {
        public RepositoryStatus? AfterCheckout { get; set; }

        public RepositoryStatus Read(string path) =>
            AfterCheckout is { } next && next.Path == path
                ? next
                : Status(path, "feature");

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

    private sealed class StubEvaluator : IExpressionEvaluator
    {
        public string? Validate(string? code) => null;

        public Task<ExpressionResult> EvaluateAsync(string? code, CancellationToken ct = default) =>
            Task.FromResult(new ExpressionResult(true, string.Empty, null));
    }

    private sealed class StubClipboard : IClipboardService
    {
        public Task<bool> SetTextAsync(string? text) => Task.FromResult(true);
    }

    private sealed class Fixture
    {
        public RecordingGitCommands Git { get; } = new();
        public StubStatusService Statuses { get; } = new();
        public RepositoriesViewModel Owner { get; }

        public Fixture(params RepositoryStatus[] initial)
        {
            var settings = new StubSettings();
            foreach (var s in initial)
                settings.Settings.TrackedRepositories.Add(new TrackedRepository { Path = s.Path, Name = s.Name });

            Owner = new RepositoriesViewModel(
                settings, Statuses, Git, new StubFolderPicker(), new StubEvaluator(), new StubClipboard(),
                new RecordingEditorService(), new FakeBranchInfo(), new FakeGitHubCli());

            for (var i = 0; i < initial.Length; i++)
                Owner.Repositories[i].Apply(initial[i]);
        }
    }
}
