using System;
using System.IO;
using LibGit2Sharp;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Exercises <see cref="GitService"/> against real temporary repositories,
/// covering the Phase 2 exit check: open a repo and resolve each comparison mode.
/// </summary>
public sealed class GitServiceTests : IDisposable
{
    private readonly string _root;
    private readonly GitService _git = new();

    public GitServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NRSGitCheckGit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _git.Dispose();
        try { DeleteDirectory(_root); } catch { /* best effort; LibGit2 may hold locks briefly */ }
    }

    [Fact]
    public void Opening_a_non_repository_folder_throws()
    {
        Assert.Throws<GitException>(() => _git.OpenRepository(_root));
    }

    [Fact]
    public void Opening_a_repo_reports_branch_and_head()
    {
        var dir = InitRepo("solo");
        Commit(dir, "a.txt", "one");

        var snapshot = _git.OpenRepository(dir);

        Assert.Equal("solo", snapshot.Name);
        Assert.False(snapshot.IsHeadUnborn);
        Assert.NotEmpty(snapshot.HeadShortSha);
        Assert.Contains(snapshot.LocalBranches, b => b.IsCurrent);
    }

    [Fact]
    public void LastCommit_resolves_to_head()
    {
        var dir = InitRepo("repo");
        var headSha = Commit(dir, "a.txt", "one");

        _git.OpenRepository(dir);
        var resolved = _git.ResolveComparison(ComparisonMode.LastCommit, null, null);

        Assert.True(resolved.Found);
        Assert.Equal(headSha, resolved.Sha);
    }

    [Fact]
    public void OtherBranch_resolves_to_that_branch_tip()
    {
        var dir = InitRepo("repo");
        var mainSha = Commit(dir, "a.txt", "one");

        using (var repo = new Repository(dir))
            repo.CreateBranch("feature");
        var featureSha = Commit(dir, "b.txt", "two"); // advances current branch only

        _git.OpenRepository(dir);
        var resolved = _git.ResolveComparison(ComparisonMode.OtherBranch, "feature", null);

        Assert.True(resolved.Found);
        Assert.Equal(mainSha, resolved.Sha);
        Assert.NotEqual(featureSha, resolved.Sha);
    }

    [Fact]
    public void BranchBase_resolves_to_merge_base()
    {
        var dir = InitRepo("repo");
        var baseSha = Commit(dir, "a.txt", "one");

        // Branch "feature" off the base, then advance both branches independently.
        using (var repo = new Repository(dir))
            Commands.Checkout(repo, repo.CreateBranch("feature"));
        Commit(dir, "feature.txt", "f");

        using (var repo = new Repository(dir))
            Commands.Checkout(repo, repo.Branches["master"] ?? repo.Branches["main"]);
        Commit(dir, "main2.txt", "m");

        _git.OpenRepository(dir);
        var resolved = _git.ResolveComparison(ComparisonMode.BranchBase, null, "feature");

        Assert.True(resolved.Found);
        Assert.Equal(baseSha, resolved.Sha);
    }

    [Fact]
    public void OtherBranch_without_selection_is_unresolved()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one");

        _git.OpenRepository(dir);
        var resolved = _git.ResolveComparison(ComparisonMode.OtherBranch, null, null);

        Assert.False(resolved.Found);
        Assert.NotNull(resolved.Error);
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
        return repo.Commit($"add {file}", sig, sig).Sha;
    }

    private static void DeleteDirectory(string path)
    {
        // Clear read-only attributes that Git sets on objects under .git.
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
