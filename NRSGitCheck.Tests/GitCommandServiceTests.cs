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
    public async Task Checking_out_a_branch_that_does_not_exist_fails()
    {
        var dir = InitRepo("missing-branch");
        Commit(dir, "a.txt", "one");

        var result = await _service.CheckoutBranchAsync(dir, "no-such-branch");

        Assert.False(result.Success);
    }

    // --- helpers ------------------------------------------------------------

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
