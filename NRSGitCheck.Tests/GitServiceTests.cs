using System;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using Xunit;
using ChangeKind = NRSGitCheck.Models.ChangeKind;

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
    public void GetChanges_reports_modified_deleted_and_untracked()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one\n");
        Commit(dir, "b.txt", "two\n");

        // Working-tree edits relative to HEAD.
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one changed\n");  // modified
        File.Delete(Path.Combine(dir, "b.txt"));                          // deleted
        File.WriteAllText(Path.Combine(dir, "c.txt"), "three\n");         // untracked

        _git.OpenRepository(dir);
        var head = _git.ResolveComparison(ComparisonMode.LastCommit, null, null).Sha!;
        var changes = _git.GetChanges(head);

        Assert.Equal(ChangeKind.Modified, Kind(changes, "a.txt"));
        Assert.Equal(ChangeKind.Deleted, Kind(changes, "b.txt"));
        Assert.Equal(ChangeKind.Untracked, Kind(changes, "c.txt"));
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void GetChanges_flags_binary_untracked_file()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one\n");
        File.WriteAllBytes(Path.Combine(dir, "blob.bin"), new byte[] { 1, 2, 0, 3, 4 });

        _git.OpenRepository(dir);
        var head = _git.ResolveComparison(ComparisonMode.LastCommit, null, null).Sha!;
        var changes = _git.GetChanges(head);

        var bin = changes.Single(c => c.Path == "blob.bin");
        Assert.True(bin.IsBinary);
        Assert.Equal(0, bin.LinesAdded);
    }

    private static ChangeKind Kind(System.Collections.Generic.IReadOnlyList<FileChange> changes, string path) =>
        changes.Single(c => c.Path == path).Kind;

    [Fact]
    public void GetFileContent_reads_old_from_rename_source_path()
    {
        var dir = InitRepo("repo");
        Commit(dir, "old-name.txt", "original\n");

        // Simulate a rename in the working tree: remove old, write new with new content.
        File.Delete(Path.Combine(dir, "old-name.txt"));
        File.WriteAllText(Path.Combine(dir, "new-name.txt"), "original\nplus more\n");

        _git.OpenRepository(dir);
        var head = _git.ResolveComparison(ComparisonMode.LastCommit, null, null).Sha!;

        // A rename change whose old content lives at the previous path.
        var renamed = new FileChange("new-name.txt", "old-name.txt", ChangeKind.Renamed, 1, 0, false);
        var content = _git.GetFileContent(head, renamed);

        Assert.Equal("original\n", content.OldText);
        Assert.Equal("original\nplus more\n", content.NewText);
        Assert.False(content.IsBinary);
    }

    [Fact]
    public void GetFileContent_flags_binary_working_file()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one\n");
        File.WriteAllBytes(Path.Combine(dir, "blob.bin"), new byte[] { 1, 0, 2, 3 });

        _git.OpenRepository(dir);
        var head = _git.ResolveComparison(ComparisonMode.LastCommit, null, null).Sha!;

        var change = new FileChange("blob.bin", null, ChangeKind.Untracked, 0, 0, true);
        var content = _git.GetFileContent(head, change);

        Assert.True(content.IsBinary);
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
