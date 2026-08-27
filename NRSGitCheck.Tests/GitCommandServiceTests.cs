using System;
using System.IO;
using System.Threading.Tasks;
using LibGit2Sharp;
using NRSGitCheck.Services;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Covers the branch checkout the Repositories tab performs. These run the real
/// <c>git</c> CLI, exactly as the application does, against temporary repositories.
/// </summary>
public sealed class GitCommandServiceTests : IDisposable
{
    private readonly string _root;
    private readonly GitCommandService _service = new();

    public GitCommandServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NRSGitCheckCmd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { DeleteDirectory(_root); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Checking_out_another_branch_moves_head()
    {
        var dir = InitRepo("switch");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");
        CreateBranch(dir, "feature");

        var result = await _service.CheckoutBranchAsync(dir, "feature");

        Assert.True(result.Success, result.Message);
        Assert.Equal("feature", CurrentBranch(dir));
    }

    [Fact]
    public async Task An_edit_that_does_not_collide_is_carried_across_the_switch()
    {
        var dir = InitRepo("carry");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");
        CreateBranch(dir, "feature");

        // Uncommitted work in a file neither branch has touched since they diverged:
        // Git takes it along rather than refusing.
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one, edited");

        var result = await _service.CheckoutBranchAsync(dir, "feature");

        Assert.True(result.Success, result.Message);
        Assert.Equal("feature", CurrentBranch(dir));
        Assert.Equal("one, edited", File.ReadAllText(Path.Combine(dir, "a.txt")));
    }

    [Fact]
    public async Task A_switch_that_would_overwrite_uncommitted_work_is_refused()
    {
        var dir = InitRepo("refuse");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");

        // feature moves a.txt on; main stays put. An uncommitted edit to the same
        // file on main then cannot survive the switch, so Git declines it.
        CreateBranch(dir, "feature");
        await _service.CheckoutBranchAsync(dir, "feature");
        Commit(dir, "a.txt", "two");
        await _service.CheckoutBranchAsync(dir, "main");

        File.WriteAllText(Path.Combine(dir, "a.txt"), "local edit");

        var result = await _service.CheckoutBranchAsync(dir, "feature");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Message);
        Assert.Equal("main", CurrentBranch(dir));                                  // still where it was
        Assert.Equal("local edit", File.ReadAllText(Path.Combine(dir, "a.txt")));  // work untouched
    }

    [Fact]
    public async Task Creating_a_branch_makes_it_and_checks_it_out()
    {
        var dir = InitRepo("create");
        Commit(dir, "a.txt", "one");

        var result = await _service.CreateBranchAsync(dir, "nrs/20260824-sa-1234-thing");

        Assert.True(result.Success, result.Message);
        Assert.Equal("nrs/20260824-sa-1234-thing", CurrentBranch(dir));
    }

    [Fact]
    public async Task Creating_a_branch_that_already_exists_fails()
    {
        var dir = InitRepo("duplicate");
        Commit(dir, "a.txt", "one");
        CreateBranch(dir, "feature");

        var result = await _service.CreateBranchAsync(dir, "feature");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public async Task Creating_a_branch_with_an_illegal_name_fails()
    {
        var dir = InitRepo("illegal");
        Commit(dir, "a.txt", "one");

        var result = await _service.CreateBranchAsync(dir, "bad..name");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Checking_out_a_branch_that_does_not_exist_fails()
    {
        var dir = InitRepo("missing-branch");
        Commit(dir, "a.txt", "one");

        var result = await _service.CheckoutBranchAsync(dir, "no-such-branch");

        Assert.False(result.Success);
    }

    // --- Push ---------------------------------------------------------------

    [Fact]
    public async Task A_first_push_creates_the_branch_on_origin_and_tracks_it()
    {
        var dir = InitRepo("firstpush");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");
        var remote = AddBareRemote(dir);

        Assert.False(HasUpstream(dir));

        var result = await _service.PushAsync(dir, "main", setUpstream: true);

        Assert.True(result.Success, result.Message);
        Assert.True(RemoteHasBranch(remote, "main"));
        Assert.True(HasUpstream(dir));
    }

    [Fact]
    public async Task A_feature_branch_the_remote_has_never_seen_can_be_published()
    {
        var dir = InitRepo("publish");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");
        var remote = AddBareRemote(dir);
        await _service.PushAsync(dir, "main", setUpstream: true);

        await _service.CheckoutBranchAsync(dir, "main");
        CreateBranch(dir, "feature");
        await _service.CheckoutBranchAsync(dir, "feature");
        Commit(dir, "b.txt", "two");

        var result = await _service.PushAsync(dir, "feature", setUpstream: true);

        Assert.True(result.Success, result.Message);
        Assert.True(RemoteHasBranch(remote, "feature"));
        Assert.Contains("upstream", result.Message);
    }

    [Fact]
    public async Task A_later_push_sends_new_commits_without_republishing()
    {
        var dir = InitRepo("secondpush");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");
        var remote = AddBareRemote(dir);
        await _service.PushAsync(dir, "main", setUpstream: true);

        var sha = Commit(dir, "b.txt", "two");

        var result = await _service.PushAsync(dir, "main", setUpstream: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(sha, RemoteTip(remote, "main"));
    }

    /// <summary>
    /// The remote moved on without this clone. Git refuses, and the refusal is passed
    /// through as a failure — nothing here retries with --force.
    /// </summary>
    [Fact]
    public async Task A_push_the_remote_rejects_is_reported_and_leaves_it_alone()
    {
        var dir = InitRepo("rejected");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");
        var remote = AddBareRemote(dir);
        await _service.PushAsync(dir, "main", setUpstream: true);

        // A second clone pushes a commit this one does not have. The bare repository's
        // HEAD still names the branch `git init` made, which the first push never
        // created, so the clone lands on nothing until main is checked out explicitly.
        var other = Path.Combine(_root, "other");
        Repository.Clone(remote, other);
        var checkout = await _service.CheckoutBranchAsync(other, "main");
        Assert.True(checkout.Success, checkout.Message);

        var theirs = Commit(other, "c.txt", "three");
        var theirPush = await _service.PushAsync(other, "main", setUpstream: false);
        Assert.True(theirPush.Success, theirPush.Message);

        Commit(dir, "b.txt", "two");
        var result = await _service.PushAsync(dir, "main", setUpstream: false);

        Assert.False(result.Success);
        Assert.Contains("reject", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(theirs, RemoteTip(remote, "main"));
    }

    [Fact]
    public async Task Pushing_with_no_repository_path_fails_cleanly()
    {
        var result = await _service.PushAsync(string.Empty, "main", setUpstream: false);

        Assert.False(result.Success);
        Assert.Contains("repository", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>Gives the repository an origin: a bare repository beside it on disk.</summary>
    private string AddBareRemote(string dir)
    {
        var remotePath = Path.Combine(_root, Path.GetFileName(dir) + ".git");
        Repository.Init(remotePath, isBare: true);

        using var repo = new Repository(dir);
        repo.Network.Remotes.Add("origin", remotePath);
        return remotePath;
    }

    private static bool RemoteHasBranch(string remotePath, string branch)
    {
        using var remote = new Repository(remotePath);
        return remote.Branches[branch] is not null;
    }

    private static string? RemoteTip(string remotePath, string branch)
    {
        using var remote = new Repository(remotePath);
        return remote.Branches[branch]?.Tip?.Sha;
    }

    private static bool HasUpstream(string dir)
    {
        using var repo = new Repository(dir);
        return repo.Head.IsTracking;
    }

    private string InitRepo(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Repository.Init(dir);
        return dir;
    }

    private static string Commit(string dir, string file, string content)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        using var repo = new Repository(dir);
        Commands.Stage(repo, file);
        var sig = new Signature("Test", "test@example.com", DateTimeOffset.Now);
        return repo.Commit($"write {file}", sig, sig).Sha;
    }

    private static void CreateBranch(string dir, string name)
    {
        using var repo = new Repository(dir);
        repo.CreateBranch(name);
    }

    private static string CurrentBranch(string dir)
    {
        using var repo = new Repository(dir);
        return repo.Head.FriendlyName;
    }

    /// <summary>Renames the checked-out branch, since `git init` may create main or master.</summary>
    private static void RenameCurrentBranch(string dir, string name)
    {
        using var repo = new Repository(dir);
        var previous = repo.Head.FriendlyName;
        if (previous == name)
            return;

        Commands.Checkout(repo, repo.CreateBranch(name));
        repo.Branches.Remove(previous);
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
