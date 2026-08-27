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
/// The Repositories tab's push action. The case worth guarding is the first push:
/// a branch the remote has never seen has no upstream, so Git reports it as zero
/// commits ahead and the row would otherwise claim there is nothing to send.
/// </summary>
public sealed class PushTests : IDisposable
{
    // --- Status reading, against real repositories --------------------------

    private readonly string _root;
    private readonly RepositoryStatusService _service = new();

    public PushTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NRSGitCheckPush", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { DeleteDirectory(_root); } catch { /* best effort; LibGit2 may hold locks briefly */ }
    }

    [Fact]
    public void A_branch_that_was_never_pushed_needs_a_first_push()
    {
        var dir = InitRepoWithRemote("first");
        CommitFile(dir, "b.txt", "two");
        CheckoutNewBranch(dir, "feature");
        CommitFile(dir, "c.txt", "three");

        var status = _service.Read(dir);

        Assert.True(status.NeedsFirstPush);
        Assert.False(status.HasUpstream);
        Assert.True(status.CanPush);
    }

    /// <summary>
    /// Without measuring against the remote's main, an unpublished branch reports
    /// zero commits ahead and the row looks like there is nothing to push.
    /// </summary>
    [Fact]
    public void An_unpublished_branch_still_counts_its_unpushed_commits()
    {
        var dir = InitRepoWithRemote("counted");
        CheckoutNewBranch(dir, "feature");
        CommitFile(dir, "c.txt", "three");
        CommitFile(dir, "d.txt", "four");

        var status = _service.Read(dir);

        Assert.Equal(2, status.AheadBy);
        Assert.True(status.HasUnpushedCommits);
        Assert.True(status.CanPush);
    }

    [Fact]
    public void A_pushed_up_to_date_branch_has_nothing_to_push()
    {
        var dir = InitRepoWithRemote("synced");

        var status = _service.Read(dir);

        Assert.True(status.HasUpstream);
        Assert.False(status.NeedsFirstPush);
        Assert.False(status.CanPush);
        Assert.Equal(0, status.AheadBy);
    }

    [Fact]
    public void A_pushed_branch_with_new_commits_can_push_without_publishing()
    {
        var dir = InitRepoWithRemote("ahead");
        CommitFile(dir, "b.txt", "two");

        var status = _service.Read(dir);

        Assert.True(status.HasUpstream);
        Assert.False(status.NeedsFirstPush);
        Assert.True(status.CanPush);
        Assert.Equal(1, status.AheadBy);
    }

    [Fact]
    public void A_repository_with_no_remote_offers_no_push()
    {
        var dir = Path.Combine(_root, "noremote");
        Directory.CreateDirectory(dir);
        Repository.Init(dir);
        CommitFile(dir, "a.txt", "one");

        var status = _service.Read(dir);

        Assert.False(status.HasRemote);
        Assert.False(status.NeedsFirstPush);
        Assert.False(status.CanPush);
    }

    // --- The row's command --------------------------------------------------

    [Fact]
    public async Task Publishing_a_new_branch_sets_the_upstream()
    {
        var fixture = new Fixture(Status(hasUpstream: false, aheadBy: 2));

        Assert.True(fixture.Row.PushToOriginCommand.CanExecute(null));
        await fixture.Row.PushToOriginCommand.ExecuteAsync(null);

        var push = Assert.Single(fixture.Git.Pushes);
        Assert.Equal(@"C:\repo", push.Path);
        Assert.Equal("feature", push.Branch);
        Assert.True(push.SetUpstream);
    }

    [Fact]
    public async Task Pushing_a_tracked_branch_does_not_touch_its_upstream()
    {
        var fixture = new Fixture(Status(hasUpstream: true, aheadBy: 3));

        await fixture.Row.PushToOriginCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(fixture.Git.Pushes).SetUpstream);
    }

    [Fact]
    public void Push_is_unavailable_when_the_branch_is_already_on_the_remote_and_level()
    {
        var fixture = new Fixture(Status(hasUpstream: true, aheadBy: 0));

        Assert.False(fixture.Row.PushToOriginCommand.CanExecute(null));
    }

    [Fact]
    public void Push_is_unavailable_without_a_remote()
    {
        var fixture = new Fixture(Status(hasUpstream: false, aheadBy: 4, hasRemote: false));

        Assert.False(fixture.Row.PushToOriginCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_rejected_push_is_reported_rather_than_forced()
    {
        var fixture = new Fixture(Status(hasUpstream: true, aheadBy: 1));
        fixture.Git.PushRefusal = "Updates were rejected because the remote contains work you do not have";

        await fixture.Row.PushToOriginCommand.ExecuteAsync(null);

        Assert.Single(fixture.Git.Pushes);
        Assert.Contains("rejected", fixture.Owner.ErrorMessage);
    }

    [Fact]
    public async Task A_successful_push_reports_what_happened_and_re_reads_the_row()
    {
        var fixture = new Fixture(Status(hasUpstream: false, aheadBy: 2));
        fixture.Statuses.Next = Status(hasUpstream: true, aheadBy: 0);

        await fixture.Row.PushToOriginCommand.ExecuteAsync(null);

        Assert.Contains("upstream", fixture.Owner.Status);
        Assert.Null(fixture.Owner.ErrorMessage);
        Assert.False(fixture.Row.NeedsFirstPush);
        Assert.False(fixture.Row.HasUnpushedCommits);
        Assert.False(fixture.Row.PushToOriginCommand.CanExecute(null));
    }

    [Fact]
    public void The_tooltip_says_which_of_the_two_pushes_will_happen()
    {
        var unpublished = new Fixture(Status(hasUpstream: false, aheadBy: 1));
        Assert.Contains("not on origin yet", unpublished.Row.PushToOriginToolTip);

        var tracked = new Fixture(Status(hasUpstream: true, aheadBy: 1));
        Assert.Contains("upstream", tracked.Row.PushToOriginToolTip);
        Assert.DoesNotContain("not on origin yet", tracked.Row.PushToOriginToolTip);
    }

    [Fact]
    public void An_unpublished_branch_does_not_read_as_clean()
    {
        var fixture = new Fixture(Status(hasUpstream: false, aheadBy: 0));

        Assert.True(fixture.Row.NeedsFirstPush);
        Assert.False(fixture.Row.IsClean);
    }

    // --- Test doubles -------------------------------------------------------

    private sealed class RecordingGitCommands : IGitCommandService
    {
        public List<(string Path, string Branch, bool SetUpstream)> Pushes { get; } = new();
        public string? PushRefusal { get; set; }

        public Task<GitCommandResult> PushAsync(
            string workingDirectory, string branch, bool setUpstream, CancellationToken ct = default)
        {
            Pushes.Add((workingDirectory, branch, setUpstream));
            return Task.FromResult(PushRefusal is { } refusal
                ? new GitCommandResult(false, refusal)
                : new GitCommandResult(true, setUpstream
                    ? $"Pushed {branch} to origin and set it as the upstream."
                    : $"Pushed {branch} to its upstream."));
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

        public Task<GitCommandResult> DiscardChangesAsync(
            string workingDirectory, bool deleteUntrackedFiles, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "discarded"));
    }

    private sealed class StubStatusService : IRepositoryStatusService
    {
        public RepositoryStatus Next { get; set; } = Status();

        public RepositoryStatus Read(string path) => Next with { Path = path };

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

    private static RepositoryStatus Status(
        bool hasUpstream = true, int aheadBy = 0, bool hasRemote = true) => new(
        Path: @"C:\repo", Name: "repo", IsValid: true, Error: null, CurrentBranch: "feature",
        IsDetachedHead: false, IsHeadUnborn: false, LocalBranches: new[] { "feature", "main" },
        MainBranch: "main", UncommittedCount: 0, HasUpstream: hasUpstream, AheadBy: aheadBy, BehindBy: 0,
        HasRemote: hasRemote, Changes: Array.Empty<WorkingTreeChange>(), UntrackedCount: 0);

    private sealed class Fixture
    {
        public RecordingGitCommands Git { get; } = new();
        public StubStatusService Statuses { get; } = new();
        public RepositoriesViewModel Owner { get; }
        public TrackedRepositoryViewModel Row { get; }

        public Fixture(RepositoryStatus initial)
        {
            var settings = new StubSettings();
            settings.Settings.TrackedRepositories.Add(new TrackedRepository { Path = @"C:\repo", Name = "repo" });

            Owner = new RepositoriesViewModel(
                settings, Statuses, Git, new StubFolderPicker(), new StubEvaluator(), new StubClipboard(),
                new RecordingEditorService());

            Statuses.Next = initial;
            Row = Owner.Repositories.Single();
            Row.Apply(initial);
        }
    }

    // --- Repository helpers -------------------------------------------------

    /// <summary>
    /// A repository whose main branch has been pushed to a bare remote on disk, so
    /// origin/main exists and later branches can be measured against it.
    /// </summary>
    private string InitRepoWithRemote(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Repository.Init(dir);
        CommitFile(dir, "a.txt", "one");

        using (var repo = new Repository(dir))
        {
            if (repo.Head.FriendlyName != "main")
            {
                var previous = repo.Head.FriendlyName;
                Commands.Checkout(repo, repo.CreateBranch("main"));
                repo.Branches.Remove(previous);
            }
        }

        var remotePath = Path.Combine(_root, name + ".git");
        Repository.Init(remotePath, isBare: true);

        using (var repo = new Repository(dir))
        {
            repo.Network.Remotes.Add("origin", remotePath);
            var main = repo.Branches["main"];
            repo.Branches.Update(main,
                b => b.Remote = "origin",
                b => b.UpstreamBranch = main.CanonicalName);
            repo.Network.Push(main);
        }

        return dir;
    }

    private static void CommitFile(string dir, string file, string content)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        using var repo = new Repository(dir);
        Commands.Stage(repo, file);
        var sig = new Signature("Test", "test@example.com", DateTimeOffset.Now);
        repo.Commit($"add {file}", sig, sig);
    }

    private static void CheckoutNewBranch(string dir, string name)
    {
        using var repo = new Repository(dir);
        Commands.Checkout(repo, repo.CreateBranch(name));
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
