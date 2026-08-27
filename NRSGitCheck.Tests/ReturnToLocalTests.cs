using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using NRSGitCheck.ViewModels;
using Xunit;
using ChangeKind = NRSGitCheck.Models.ChangeKind;

namespace NRSGitCheck.Tests;

/// <summary>
/// Leaving a pull request review: the toolbar only offers the way back while a
/// <c>pr-N</c> branch is checked out, and taking it checks the previous branch out
/// and puts the comparison back on uncommitted changes.
/// </summary>
public sealed class ReturnToLocalTests : IDisposable
{
    private readonly string _dir;

    public ReturnToLocalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NRSGitCheckLocal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("pr-1", true)]
    [InlineData("pr-4321", true)]
    [InlineData("pr-0", false)]
    [InlineData("pr-", false)]
    [InlineData("pr-12a", false)]
    [InlineData("feature/pr-12", false)]
    [InlineData("main", false)]
    [InlineData(null, false)]
    public void Pull_request_branches_are_recognized(string? branch, bool expected)
    {
        Assert.Equal(expected, PullRequestReference.IsPullRequestBranch(branch));
    }

    [Fact]
    public async Task On_a_normal_branch_the_review_is_not_in_progress()
    {
        var vm = await OpenOn("feature/login");

        Assert.False(vm.IsReviewingPullRequest);
        Assert.False(vm.ReturnToLocalCommand.CanExecute(null));
    }

    [Fact]
    public async Task On_a_pr_branch_the_way_back_is_offered()
    {
        var vm = await OpenOn("pr-42");

        Assert.True(vm.IsReviewingPullRequest);
        Assert.True(vm.ReturnToLocalCommand.CanExecute(null));
    }

    [Fact]
    public async Task Returning_checks_out_main_when_the_starting_branch_is_unknown()
    {
        // A review picked up in a later session: nothing recorded where it began.
        var commands = new RecordingGitCommands();
        var vm = await OpenOn("pr-42", commands);

        await vm.ReturnToLocalCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "main" }, commands.CheckedOut);
    }

    [Fact]
    public async Task Returning_puts_the_comparison_back_on_uncommitted_changes()
    {
        var commands = new RecordingGitCommands();
        var vm = await OpenOn("pr-42", commands);
        vm.SelectedMode = vm.ComparisonModes.First(m => m.Mode == ComparisonMode.VsMain);

        await vm.ReturnToLocalCommand.ExecuteAsync(null);

        Assert.Equal(ComparisonMode.LastCommit, vm.SelectedMode.Mode);
    }

    [Fact]
    public async Task A_refused_checkout_leaves_the_review_in_place_and_says_why()
    {
        var commands = new RecordingGitCommands { CheckoutSucceeds = false };
        var vm = await OpenOn("pr-42", commands);

        await vm.ReturnToLocalCommand.ExecuteAsync(null);

        Assert.Equal("your local changes would be overwritten", vm.ErrorMessage);
        Assert.True(vm.IsReviewingPullRequest);
    }

    [Fact]
    public async Task A_remote_only_main_returns_to_the_local_branch_name()
    {
        var commands = new RecordingGitCommands();
        var vm = await OpenOn("pr-42", commands, mainBranch: "origin/master");

        await vm.ReturnToLocalCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "master" }, commands.CheckedOut);
    }

    [Fact]
    public async Task A_clean_tree_offers_the_pull_request_review()
    {
        var vm = await OpenOn("feature/login");

        Assert.False(vm.HasLocalChanges);
        Assert.True(vm.OpenPullRequestDialogCommand.CanExecute(null));
    }

    [Fact]
    public async Task Local_changes_block_the_pull_request_review()
    {
        var vm = await OpenOn("feature/login", uncommitted: 3);

        Assert.True(vm.HasLocalChanges);
        Assert.False(vm.OpenPullRequestDialogCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_blocked_review_says_what_is_in_the_way()
    {
        var vm = await OpenOn("feature/login", uncommitted: 1);

        Assert.Equal("1 local change", vm.LocalChangesBadgeText);
        Assert.Contains("1 local change", vm.ReviewPullRequestToolTip);

        var many = await OpenOn("feature/login", uncommitted: 2);
        Assert.Equal("2 local changes", many.LocalChangesBadgeText);
    }

    // --- harness ------------------------------------------------------------

    private async Task<MainWindowViewModel> OpenOn(
        string branch, RecordingGitCommands? commands = null, string mainBranch = "main",
        int uncommitted = 0)
    {
        var settings = new StubSettings();
        settings.Settings.ReopenLastRepoOnLaunch = true;
        settings.Settings.RecentRepositories.Add(new RecentRepository { Path = _dir, Name = "repo" });

        var vm = new MainWindowViewModel(
            settings,
            new StubGit(_dir, branch, mainBranch, uncommitted),
            commands ?? new RecordingGitCommands(),
            new StubFolderPicker(),
            new DiffViewModel(new StubDiff(), settings, new StubClipboard()),
            new StubTheme(),
            new RepositoriesViewModel(
                settings, new StubRepositoryStatus(), new RecordingGitCommands(),
                new StubFolderPicker(), new RoslynExpressionEvaluator(), new StubClipboard()));

        await vm.InitializeAsync();
        return vm;
    }

    private sealed class RecordingGitCommands : IGitCommandService
    {
        public List<string> CheckedOut { get; } = new();
        public bool CheckoutSucceeds { get; init; } = true;

        public Task<GitCommandResult> CheckoutBranchAsync(
            string workingDirectory, string branch, CancellationToken ct = default)
        {
            if (!CheckoutSucceeds)
                return Task.FromResult(new GitCommandResult(false, "your local changes would be overwritten"));

            CheckedOut.Add(branch);
            return Task.FromResult(new GitCommandResult(true, $"checked out {branch}"));
        }

        public Task<GitCommandResult> PullMainAsync(
            string workingDirectory, string? mainBranch, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "up to date"));

        public Task<GitCommandResult> CheckoutPullRequestAsync(
            string workingDirectory, PullRequestReference pr, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"checked out pr-{pr.Number}"));

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

    private sealed class StubGit : IGitService
    {
        private readonly string _dir;
        private readonly string _branch;
        private readonly string _mainBranch;
        private readonly int _uncommitted;

        public StubGit(string dir, string branch, string mainBranch, int uncommitted = 0)
        {
            _dir = dir;
            _branch = branch;
            _mainBranch = mainBranch;
            _uncommitted = uncommitted;
        }

        public int GetUncommittedChangeCount() => _uncommitted;

        public RepositorySnapshot OpenRepository(string path) => new(
            _dir, "repo", _branch, false, false, "abc1234",
            new[]
            {
                new BranchInfo(_branch, "abc1234", "abc1234", true),
                new BranchInfo("main", "def5678", "def5678", false),
            },
            "main", _mainBranch, true, "https://github.com/owner/repo.git");

        public ResolvedComparison ResolveComparison(
            ComparisonMode mode, string? otherBranch, string? parentBranch, string? commitSha = null) =>
            ResolvedComparison.Resolved("abc1234", "last commit (abc1234)");

        public IReadOnlyList<CommitInfo> GetBranchCommits(string? mainBranch, int maxCount = 200) =>
            Array.Empty<CommitInfo>();

        public IReadOnlyList<FileChange> GetChanges(string baseCommitSha) => Array.Empty<FileChange>();

        public IReadOnlyDictionary<string, FileStats> GetChangeStats(string baseCommitSha) =>
            new Dictionary<string, FileStats>();

        public FileContent GetFileContent(string baseCommitSha, FileChange change) =>
            new(string.Empty, string.Empty, false, false);
    }

    private sealed class StubRepositoryStatus : IRepositoryStatusService
    {
        public NRSGitCheck.Models.RepositoryStatus Read(string path) =>
            NRSGitCheck.Models.RepositoryStatus.Failed(path, "stub", "not read");

        public Task<IReadOnlyList<NRSGitCheck.Models.RepositoryStatus>> ReadAllAsync(
            IEnumerable<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NRSGitCheck.Models.RepositoryStatus>>(paths.Select(Read).ToList());
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

    private sealed class StubClipboard : IClipboardService
    {
        public Task<bool> SetTextAsync(string? text) => Task.FromResult(true);
    }

    private sealed class StubFolderPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }

    private sealed class StubTheme : IThemeService
    {
        public ThemeMode Mode => ThemeMode.System;
        public void Initialize() { }
        public void SetMode(ThemeMode mode) { }
        public event Action? EffectiveThemeChanged { add { } remove { } }
    }

    private sealed class StubDiff : IDiffService
    {
        public DiffStream BuildDiffStream(
            string baseCommitSha, FileChange change, int contextLines = 3, bool wholeFile = false) =>
            new() { Hunks = Array.Empty<DiffHunk>() };
    }
}
