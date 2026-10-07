using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using NRSGitCheck.ViewModels;
using Xunit;
using RepositoryStatus = NRSGitCheck.Models.RepositoryStatus;

namespace NRSGitCheck.Tests;

/// <summary>
/// The per-repository branch list: local branches with their last commit, the pull
/// request for each when the GitHub CLI is available, the banner when it is not,
/// and the two-step delete.
/// </summary>
public sealed class BranchListTests : IDisposable
{
    private readonly string _root;

    public BranchListTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NRSGitCheckBranches", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { DeleteDirectory(_root); } catch { /* best effort; LibGit2 may hold locks briefly */ }
    }

    // --- Listing ------------------------------------------------------------

    [Fact]
    public async Task Opening_lists_every_local_branch()
    {
        var f = new Fixture(Branch("feature"), Branch("main", isMain: true, isCurrent: true));

        await f.OpenAsync();

        Assert.True(f.List.IsVisible);
        Assert.Equal(new[] { "feature", "main" }, f.List.Branches.Select(b => b.Name));
        Assert.Equal("2 local branches", f.List.Summary);
        Assert.Equal("repo", f.List.RepositoryName);
        Assert.False(f.List.IsLoading);
    }

    [Fact]
    public async Task An_unreadable_repository_says_why()
    {
        var f = new Fixture();
        f.Branches.Failure = "Not a Git repository.";

        await f.OpenAsync();

        Assert.Empty(f.List.Branches);
        Assert.Contains("Not a Git repository.", f.List.Error);
    }

    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "yesterday")]
    [InlineData(5, "5 days ago")]
    [InlineData(21, "3 weeks ago")]
    [InlineData(95, "3 months ago")]
    [InlineData(400, "1 year ago")]
    [InlineData(1100, "3 years ago")]
    public void Ages_are_coarse_and_readable(int daysAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, BranchItemViewModel.FormatAge(now.AddDays(-daysAgo), now));
    }

    // --- Pull requests ------------------------------------------------------

    [Fact]
    public async Task Without_the_github_cli_the_list_says_how_to_get_pull_requests()
    {
        var f = new Fixture(Branch("feature"));

        await f.OpenAsync(GitHubCliStatus.NotInstalled);

        Assert.True(f.List.HasPullRequestNote);
        Assert.Contains("Install the GitHub CLI", f.List.PullRequestNote);
        Assert.False(f.List.Branches[0].HasPullRequest);
        Assert.Equal("—", f.List.Branches[0].PullRequestPlaceholder);
    }

    [Fact]
    public async Task Signed_out_the_list_says_to_sign_in()
    {
        var f = new Fixture(Branch("feature"));

        await f.OpenAsync(GitHubCliStatus.NotSignedIn);

        Assert.Contains("gh auth login", f.List.PullRequestNote);
    }

    [Fact]
    public async Task With_the_github_cli_each_branch_shows_its_pull_request()
    {
        var f = new Fixture(Branch("feature"), Branch("done"), Branch("lonely"));
        f.GitHub.PullRequests.Add(Pr(12, "feature", PullRequestState.Open));
        f.GitHub.PullRequests.Add(Pr(9, "done", PullRequestState.Merged));

        await f.OpenAsync(GitHubCliStatus.Available);

        var feature = f.Item("feature");
        Assert.Equal("#12", feature.PullRequestText);
        Assert.Equal("Open", feature.PullRequestStateText);
        Assert.True(feature.IsPullRequestOpen);

        var done = f.Item("done");
        Assert.Equal("Merged", done.PullRequestStateText);
        Assert.True(done.IsPullRequestMerged);

        Assert.False(f.Item("lonely").HasPullRequest);
        Assert.Equal("No PR", f.Item("lonely").PullRequestPlaceholder);
        Assert.False(f.List.HasPullRequestNote);
    }

    [Fact]
    public async Task A_github_failure_is_shown_and_the_branches_stay()
    {
        var f = new Fixture(Branch("feature"));
        f.GitHub.ListError = "none of the git remotes point to a known GitHub host";

        await f.OpenAsync(GitHubCliStatus.Available);

        Assert.Single(f.List.Branches);
        Assert.Contains("known GitHub host", f.List.PullRequestNote);
    }

    [Fact]
    public void A_branch_matches_the_newest_pull_request_from_it()
    {
        var prs = new[]
        {
            Pr(3, "feature", PullRequestState.Closed),
            Pr(8, "feature", PullRequestState.Open),
        };

        Assert.Equal(8, PullRequestInfo.FindFor("feature", prs)!.Number);
    }

    [Fact]
    public void A_fork_with_the_same_branch_name_is_not_a_match()
    {
        var prs = new[] { Pr(5, "main", PullRequestState.Open, fork: true) };

        Assert.Null(PullRequestInfo.FindFor("main", prs));
    }

    [Fact]
    public void A_review_branch_maps_to_its_pull_request_by_number()
    {
        // pr-N branches are what this app creates to review someone else's PR,
        // usually from a fork, so the number is what links them.
        var prs = new[] { Pr(42, "their-branch", PullRequestState.Merged, fork: true) };

        Assert.Equal(42, PullRequestInfo.FindFor("pr-42", prs)!.Number);
    }

    [Fact]
    public void Gh_json_is_read_into_pull_requests()
    {
        const string json = """
            [
              {"number":7,"title":"Add login","state":"MERGED","isDraft":false,
               "headRefName":"feature/login","isCrossRepository":false,"url":"https://github.com/o/r/pull/7"},
              {"number":8,"title":"WIP","state":"OPEN","isDraft":true,
               "headRefName":"wip","isCrossRepository":true,"url":"https://github.com/o/r/pull/8"}
            ]
            """;

        var prs = GitHubCliService.ParsePullRequests(json);

        Assert.Equal(2, prs.Count);
        Assert.Equal(new PullRequestInfo(7, "Add login", PullRequestState.Merged, false, "feature/login", false,
            "https://github.com/o/r/pull/7"), prs[0]);
        Assert.True(prs[1].IsDraft);
        Assert.True(prs[1].IsCrossRepository);
    }

    [Fact]
    public void Gh_is_found_in_its_install_folder_or_on_path()
    {
        var exe = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        var installed = Path.Combine(@"C:\Program Files", "GitHub CLI", exe);
        var onPath = Path.Combine(@"C:\tools", exe);

        string? Env(string name) => name switch
        {
            "ProgramFiles" => @"C:\Program Files",
            "PATH" => @"C:\tools",
            _ => null,
        };

        Assert.Equal(installed, GitHubCliService.Locate(p => p == installed || p == onPath, Env));
        Assert.Equal(onPath, GitHubCliService.Locate(p => p == onPath, Env));
        Assert.Null(GitHubCliService.Locate(_ => false, Env));
    }

    // --- Banner -------------------------------------------------------------

    [Fact]
    public async Task A_missing_github_cli_shows_the_banner()
    {
        var f = new Fixture();
        f.GitHub.Status = GitHubCliStatus.NotInstalled;

        await f.Owner.EnsureLoadedAsync();

        Assert.True(f.Owner.ShowGitHubBanner);
        Assert.Contains("was not found", f.Owner.GitHubBannerText);
    }

    [Fact]
    public async Task A_signed_out_github_cli_shows_the_banner_asking_to_sign_in()
    {
        var f = new Fixture();
        f.GitHub.Status = GitHubCliStatus.NotSignedIn;

        await f.Owner.EnsureLoadedAsync();

        Assert.True(f.Owner.ShowGitHubBanner);
        Assert.Contains("not signed in", f.Owner.GitHubBannerText);
    }

    [Fact]
    public async Task A_working_github_cli_shows_no_banner()
    {
        var f = new Fixture();
        f.GitHub.Status = GitHubCliStatus.Available;

        await f.Owner.EnsureLoadedAsync();

        Assert.False(f.Owner.ShowGitHubBanner);
    }

    [Fact]
    public async Task Checking_again_after_installing_clears_the_banner()
    {
        var f = new Fixture();
        await f.Owner.EnsureLoadedAsync();
        Assert.True(f.Owner.ShowGitHubBanner);

        f.GitHub.Status = GitHubCliStatus.Available;
        await f.Owner.RecheckGitHubCliCommand.ExecuteAsync(null);

        Assert.False(f.Owner.ShowGitHubBanner);
        Assert.Equal(2, f.GitHub.Checks);
    }

    [Fact]
    public async Task The_banner_can_be_dismissed()
    {
        var f = new Fixture();
        await f.Owner.EnsureLoadedAsync();

        f.Owner.DismissGitHubBannerCommand.Execute(null);

        Assert.False(f.Owner.ShowGitHubBanner);
    }

    // --- Delete -------------------------------------------------------------

    [Fact]
    public async Task The_checked_out_branch_and_main_cannot_be_deleted()
    {
        var f = new Fixture(Branch("current", isCurrent: true), Branch("main", isMain: true), Branch("old"));
        await f.OpenAsync();

        Assert.False(f.Item("current").CanDelete);
        Assert.False(f.Item("current").BeginDeleteCommand.CanExecute(null));
        Assert.False(f.Item("main").CanDelete);
        Assert.True(f.Item("old").CanDelete);
    }

    [Fact]
    public async Task Deleting_asks_first()
    {
        var f = new Fixture(Branch("old"));
        await f.OpenAsync();

        f.Item("old").BeginDeleteCommand.Execute(null);

        Assert.True(f.Item("old").IsConfirmingDelete);
        Assert.Empty(f.Git.Deletes);
    }

    [Fact]
    public async Task A_confirmed_delete_is_a_safe_delete_and_removes_the_line()
    {
        var f = new Fixture(Branch("old"), Branch("keep"));
        await f.OpenAsync();

        var old = f.Item("old");
        old.BeginDeleteCommand.Execute(null);
        await old.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal(("old", false), Assert.Single(f.Git.Deletes));
        Assert.Equal(new[] { "keep" }, f.List.Branches.Select(b => b.Name));
        Assert.Equal("1 local branch", f.List.Summary);
        Assert.Contains("Deleted branch old", f.Owner.Status);
        Assert.True(f.Statuses.Reads > 0, "the repository row should be re-read so its branch picker drops the branch");
    }

    [Fact]
    public async Task An_unmerged_branch_asks_before_force_deleting()
    {
        var f = new Fixture(Branch("wip"));
        f.Git.Unmerged.Add("wip");
        await f.OpenAsync();

        var wip = f.Item("wip");
        wip.BeginDeleteCommand.Execute(null);
        await wip.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.True(wip.NeedsForceDelete);
        Assert.False(wip.IsConfirmingDelete);
        Assert.Null(wip.DeleteError);
        Assert.Contains(f.List.Branches, b => b.Name == "wip");
        Assert.Contains("not in main", wip.ForceDeleteExplanation);

        await wip.ForceDeleteCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ("wip", false), ("wip", true) }, f.Git.Deletes);
        Assert.Empty(f.List.Branches);
    }

    [Fact]
    public async Task A_squash_merged_branch_explains_why_git_hesitates()
    {
        var f = new Fixture(Branch("squashed"));
        f.Git.Unmerged.Add("squashed");
        f.GitHub.PullRequests.Add(Pr(31, "squashed", PullRequestState.Merged));
        await f.OpenAsync(GitHubCliStatus.Available);

        var item = f.Item("squashed");
        item.BeginDeleteCommand.Execute(null);
        await item.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Contains("PR #31 was merged", item.ForceDeleteExplanation);
    }

    [Fact]
    public async Task Backing_out_of_a_delete_leaves_the_branch()
    {
        var f = new Fixture(Branch("old"));
        await f.OpenAsync();

        var old = f.Item("old");
        old.BeginDeleteCommand.Execute(null);
        old.CancelDeleteCommand.Execute(null);

        Assert.False(old.IsConfirmingDelete);
        Assert.Empty(f.Git.Deletes);
    }

    [Fact]
    public async Task Any_other_refusal_is_reported_on_the_line()
    {
        var f = new Fixture(Branch("locked"));
        f.Git.Refusals["locked"] = "error: cannot lock ref 'refs/heads/locked'";
        await f.OpenAsync();

        var item = f.Item("locked");
        await item.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal("error: cannot lock ref 'refs/heads/locked'", item.DeleteError);
        Assert.False(item.NeedsForceDelete);
        Assert.NotNull(f.Owner.ErrorMessage);
    }

    // --- Against real repositories ------------------------------------------

    [Fact]
    public void Real_branches_report_current_main_merged_and_gone()
    {
        var dir = InitRepo("real");
        CommitFile(dir, "a.txt", "one");
        CreateBranch(dir, "merged-in");          // nothing beyond main
        CreateBranch(dir, "ahead");
        Checkout(dir, "ahead");
        CommitFile(dir, "b.txt", "two");          // a commit main lacks
        Checkout(dir, "main");
        CreateBranch(dir, "gone");

        using (var repo = new Repository(dir))
        {
            repo.Network.Remotes.Add("origin", Path.Combine(_root, "nowhere.git"));
            repo.Config.Set("branch.gone.remote", "origin");
            repo.Config.Set("branch.gone.merge", "refs/heads/gone");
        }

        var branches = new BranchInfoService().ReadLocalBranches(dir).ToDictionary(b => b.Name);

        Assert.True(branches["main"].IsCurrent);
        Assert.True(branches["main"].IsMainBranch);
        Assert.False(branches["main"].IsMergedIntoMain);
        Assert.True(branches["merged-in"].IsMergedIntoMain);
        Assert.False(branches["ahead"].IsMergedIntoMain);
        Assert.True(branches["gone"].IsUpstreamGone);
        Assert.False(branches["ahead"].IsUpstreamGone);
        Assert.NotNull(branches["ahead"].LastCommitDate);
    }

    [Fact]
    public async Task Git_refuses_a_safe_delete_of_unmerged_work_and_force_removes_it()
    {
        var dir = InitRepo("delete");
        CommitFile(dir, "a.txt", "one");
        CreateBranch(dir, "wip");
        Checkout(dir, "wip");
        CommitFile(dir, "b.txt", "two");
        Checkout(dir, "main");

        var git = new GitCommandService();

        var safe = await git.DeleteBranchAsync(dir, "wip", force: false);
        Assert.False(safe.Success);
        Assert.True(safe.IsNotFullyMerged, safe.Message);

        var forced = await git.DeleteBranchAsync(dir, "wip", force: true);
        Assert.True(forced.Success, forced.Message);
        Assert.Contains("wip", forced.Message);

        using var repo = new Repository(dir);
        Assert.Null(repo.Branches["wip"]);
    }

    [Fact]
    public async Task Git_safely_deletes_a_merged_branch()
    {
        var dir = InitRepo("merged");
        CommitFile(dir, "a.txt", "one");
        CreateBranch(dir, "done");

        var result = await new GitCommandService().DeleteBranchAsync(dir, "done", force: false);

        Assert.True(result.Success, result.Message);
        using var repo = new Repository(dir);
        Assert.Null(repo.Branches["done"]);
    }

    // --- Helpers ------------------------------------------------------------

    private static LocalBranchInfo Branch(
        string name, bool isCurrent = false, bool isMain = false, bool merged = false, bool gone = false) =>
        new(name, DateTimeOffset.Now.AddDays(-2), isCurrent, isMain, merged, gone);

    private static PullRequestInfo Pr(int number, string head, PullRequestState state, bool fork = false) =>
        new(number, $"PR {number}", state, false, head, fork, $"https://github.com/o/r/pull/{number}");

    private sealed class Fixture
    {
        public FakeBranchInfo Branches { get; } = new();
        public FakeGitHubCli GitHub { get; } = new();
        public RecordingGitCommands Git { get; } = new();
        public CountingStatusService Statuses { get; } = new();
        public RepositoriesViewModel Owner { get; }
        public TrackedRepositoryViewModel Row { get; }
        public BranchListViewModel List => Owner.BranchList;

        public Fixture(params LocalBranchInfo[] branches)
        {
            Branches.Branches.AddRange(branches);

            var settings = new StubSettings();
            settings.Settings.TrackedRepositories.Add(new TrackedRepository { Path = @"C:\repo", Name = "repo" });

            Owner = new RepositoriesViewModel(
                settings, Statuses, Git, new StubFolderPicker(), new StubEvaluator(), new StubClipboard(),
                new RecordingEditorService(), Branches, GitHub);

            Row = Owner.Repositories.Single();
            Row.Apply(CountingStatusService.Status(@"C:\repo"));
        }

        public Task OpenAsync(GitHubCliStatus gitHub = GitHubCliStatus.NotInstalled)
        {
            GitHub.Status = gitHub;
            return List.OpenAsync(Row, Task.FromResult(gitHub));
        }

        public BranchItemViewModel Item(string name) => List.Branches.Single(b => b.Name == name);
    }

    private sealed class RecordingGitCommands : IGitCommandService
    {
        public List<(string Branch, bool Force)> Deletes { get; } = new();

        /// <summary>Branches a safe delete refuses as not fully merged.</summary>
        public HashSet<string> Unmerged { get; } = new();

        /// <summary>Branches any delete refuses, with Git's message.</summary>
        public Dictionary<string, string> Refusals { get; } = new();

        public Task<GitCommandResult> DeleteBranchAsync(
            string workingDirectory, string branch, bool force, CancellationToken ct = default)
        {
            Deletes.Add((branch, force));

            if (Refusals.TryGetValue(branch, out var refusal))
                return Task.FromResult(new GitCommandResult(false, refusal));
            if (!force && Unmerged.Contains(branch))
                return Task.FromResult(new GitCommandResult(false, $"error: the branch '{branch}' is not fully merged."));

            return Task.FromResult(new GitCommandResult(true, $"Deleted branch {branch} (was 1a2b3c4)."));
        }

        public Task<GitCommandResult> PullMainAsync(
            string workingDirectory, string? mainBranch, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "up to date"));

        public Task<GitCommandResult> CheckoutPullRequestAsync(
            string workingDirectory, PullRequestReference pr, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "checked out"));

        public Task<GitCommandResult> CheckoutBranchAsync(
            string workingDirectory, string branch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"checked out {branch}"));

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

    private sealed class CountingStatusService : IRepositoryStatusService
    {
        public int Reads { get; private set; }

        public RepositoryStatus Read(string path)
        {
            Reads++;
            return Status(path);
        }

        public Task<IReadOnlyList<RepositoryStatus>> ReadAllAsync(
            IEnumerable<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepositoryStatus>>(paths.Select(Read).ToList());

        public static RepositoryStatus Status(string path) => new(
            Path: path, Name: "repo", IsValid: true, Error: null, CurrentBranch: "main",
            IsDetachedHead: false, IsHeadUnborn: false, LocalBranches: new[] { "main" },
            MainBranch: "main", UncommittedCount: 0, HasUpstream: true, AheadBy: 0, BehindBy: 0,
            HasRemote: true, Changes: Array.Empty<WorkingTreeChange>(), UntrackedCount: 0);
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

    private string InitRepo(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Repository.Init(dir);
        return dir;
    }

    /// <summary>Commits a file, naming the first branch "main" whatever the machine's default is.</summary>
    private static void CommitFile(string dir, string file, string content)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        using var repo = new Repository(dir);
        var unborn = repo.Info.IsHeadUnborn;
        if (unborn)
            repo.Refs.UpdateTarget("HEAD", "refs/heads/main");

        Commands.Stage(repo, file);
        var sig = new Signature("Test", "test@example.com", DateTimeOffset.Now);
        repo.Commit($"add {file}", sig, sig);
    }

    private static void CreateBranch(string dir, string name)
    {
        using var repo = new Repository(dir);
        repo.CreateBranch(name);
    }

    private static void Checkout(string dir, string name)
    {
        using var repo = new Repository(dir);
        Commands.Checkout(repo, repo.Branches[name]);
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
