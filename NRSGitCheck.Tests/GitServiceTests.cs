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

    [Fact]
    public void GetChangeStats_reports_line_counts_for_tracked_changes()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one\ntwo\n");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "one\ntwo\nthree\n"); // +1 line
        File.WriteAllText(Path.Combine(dir, "c.txt"), "new\n");             // untracked

        _git.OpenRepository(dir);
        var head = _git.ResolveComparison(ComparisonMode.LastCommit, null, null).Sha!;

        // The fast list carries no tracked counts yet...
        var changes = _git.GetChanges(head);
        Assert.Equal(0, changes.Single(c => c.Path == "a.txt").LinesAdded);

        // ...the background stats pass provides them, and omits untracked files.
        var stats = _git.GetChangeStats(head);
        Assert.Equal(1, stats["a.txt"].LinesAdded);
        Assert.False(stats.ContainsKey("c.txt"));
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

    [Fact]
    public void SinceCommit_resolves_to_the_chosen_commit()
    {
        var dir = InitRepo("repo");
        var first = Commit(dir, "a.txt", "one\n");
        Commit(dir, "b.txt", "two\n");

        _git.OpenRepository(dir);
        var resolved = _git.ResolveComparison(ComparisonMode.SinceCommit, null, null, first);

        Assert.True(resolved.Found);
        Assert.Equal(first, resolved.Sha);
    }

    [Fact]
    public void SinceCommit_without_a_commit_is_unresolved()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one\n");

        _git.OpenRepository(dir);
        var resolved = _git.ResolveComparison(ComparisonMode.SinceCommit, null, null, null);

        Assert.False(resolved.Found);
        Assert.NotNull(resolved.Error);
    }

    [Fact]
    public void SinceCommit_diff_spans_every_commit_after_the_chosen_one()
    {
        var dir = InitRepo("repo");
        var first = Commit(dir, "a.txt", "one\n");
        Commit(dir, "b.txt", "two\n");                              // committed after `first`
        File.WriteAllText(Path.Combine(dir, "c.txt"), "three\n");    // still uncommitted

        _git.OpenRepository(dir);
        var sha = _git.ResolveComparison(ComparisonMode.SinceCommit, null, null, first).Sha!;
        var changes = _git.GetChanges(sha);

        // Both the later commit and the uncommitted file show up.
        Assert.Contains(changes, c => c.Path == "b.txt" && c.Kind == ChangeKind.Added);
        Assert.Contains(changes, c => c.Path == "c.txt" && c.Kind == ChangeKind.Untracked);
    }

    [Fact]
    public void VsMain_resolves_to_the_merge_base_with_main()
    {
        var dir = InitRepo("repo");
        var baseSha = Commit(dir, "a.txt", "one\n");
        RenameCurrentBranch(dir, "main");

        // Branch off, then advance both sides so the merge base is neither tip.
        using (var repo = new Repository(dir))
            Commands.Checkout(repo, repo.CreateBranch("feature"));
        Commit(dir, "feature.txt", "f");

        using (var repo = new Repository(dir))
            Commands.Checkout(repo, repo.Branches["main"]);
        Commit(dir, "main2.txt", "m");

        using (var repo = new Repository(dir))
            Commands.Checkout(repo, repo.Branches["feature"]);

        _git.OpenRepository(dir);
        var resolved = _git.ResolveComparison(ComparisonMode.VsMain, null, null);

        Assert.True(resolved.Found);
        Assert.Equal(baseSha, resolved.Sha);
        Assert.Contains("main", resolved.Label);
    }

    [Fact]
    public void VsMain_is_unresolved_when_there_is_no_main_or_master()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one\n");
        RenameCurrentBranch(dir, "trunk");

        _git.OpenRepository(dir);
        var resolved = _git.ResolveComparison(ComparisonMode.VsMain, null, null);

        Assert.False(resolved.Found);
        Assert.NotNull(resolved.Error);
    }

    [Fact]
    public void GetBranchCommits_stops_at_the_branch_point()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one\n");
        var branchPoint = Commit(dir, "b.txt", "two\n");
        RenameCurrentBranch(dir, "main");

        using (var repo = new Repository(dir))
            Commands.Checkout(repo, repo.CreateBranch("feature"));
        var f1 = Commit(dir, "f1.txt", "f1");
        var f2 = Commit(dir, "f2.txt", "f2");

        _git.OpenRepository(dir);
        var commits = _git.GetBranchCommits("main");

        // Newest first, back to and including the branch point; nothing older.
        Assert.Equal(new[] { f2, f1, branchPoint }, commits.Select(c => c.Sha));
        Assert.True(commits[^1].IsBranchStart);
        Assert.All(commits.Take(2), c => Assert.False(c.IsBranchStart));
    }

    [Fact]
    public void GetBranchCommits_falls_back_to_recent_history_when_on_main()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one\n");
        Commit(dir, "b.txt", "two\n");
        RenameCurrentBranch(dir, "main");

        _git.OpenRepository(dir);
        var commits = _git.GetBranchCommits("main");

        // The merge base with main is HEAD itself, so the picker shows plain history
        // rather than a single useless entry.
        Assert.Equal(2, commits.Count);
        Assert.All(commits, c => Assert.False(c.IsBranchStart));
    }

    [Fact]
    public void GetBranchCommits_respects_maxCount()
    {
        var dir = InitRepo("repo");
        for (var i = 0; i < 5; i++)
            Commit(dir, $"f{i}.txt", $"{i}");

        _git.OpenRepository(dir);
        var commits = _git.GetBranchCommits(null, maxCount: 3);

        Assert.Equal(3, commits.Count);
    }

    [Fact]
    public void Snapshot_reports_the_detected_main_branch()
    {
        var dir = InitRepo("repo");
        Commit(dir, "a.txt", "one\n");
        RenameCurrentBranch(dir, "main");

        var snapshot = _git.OpenRepository(dir);

        Assert.Equal("main", snapshot.MainBranch);
        Assert.False(snapshot.HasRemote);
    }

    [Fact]
    public void Untracked_line_counts_survive_the_chunked_read()
    {
        var dir = InitRepo("repo");
        Commit(dir, "seed.txt", "seed\n");

        // Larger than the 64 KB read buffer, so the count spans several chunks.
        var big = string.Concat(Enumerable.Repeat("a line of text\n", 20_000));
        File.WriteAllText(Path.Combine(dir, "many.txt"), big);

        // No trailing newline: the final partial line still counts.
        File.WriteAllText(Path.Combine(dir, "tail.txt"), "one\ntwo\nthree");

        // Exactly one chunk boundary's worth, ending on a newline.
        File.WriteAllText(Path.Combine(dir, "exact.txt"), "x\n");

        _git.OpenRepository(dir);
        var head = _git.ResolveComparison(ComparisonMode.LastCommit, null, null).Sha!;
        var changes = _git.GetChanges(head).ToDictionary(c => c.Path, c => c);

        Assert.Equal(20_000, changes["many.txt"].LinesAdded);
        Assert.Equal(3, changes["tail.txt"].LinesAdded);
        Assert.Equal(1, changes["exact.txt"].LinesAdded);
        Assert.All(new[] { "many.txt", "tail.txt", "exact.txt" },
            p => Assert.False(changes[p].IsBinary));
    }

    [Fact]
    public void An_untracked_binary_file_is_flagged_and_not_counted()
    {
        var dir = InitRepo("repo");
        Commit(dir, "seed.txt", "seed\n");

        var bytes = new byte[4096];
        bytes[10] = 0;   // NUL in the probe window
        File.WriteAllBytes(Path.Combine(dir, "blob.bin"), bytes);

        _git.OpenRepository(dir);
        var head = _git.ResolveComparison(ComparisonMode.LastCommit, null, null).Sha!;
        var change = _git.GetChanges(head).Single(c => c.Path == "blob.bin");

        Assert.True(change.IsBinary);
        Assert.Equal(0, change.LinesAdded);
    }

    [Fact]
    public void A_very_large_untracked_file_is_listed_but_not_counted()
    {
        var dir = InitRepo("repo");
        Commit(dir, "seed.txt", "seed\n");

        // Over the 4 MB counting cap: re-reading these on every refresh is what made
        // auto-refresh crawl, and the diff view will not render them anyway.
        var path = Path.Combine(dir, "huge.log");
        using (var fs = File.Create(path))
        {
            var chunk = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("line\n", 1000)));
            for (var i = 0; i < 900; i++)   // ~4.5 MB
                fs.Write(chunk, 0, chunk.Length);
        }
        Assert.True(new FileInfo(path).Length > 4L * 1024 * 1024);

        _git.OpenRepository(dir);
        var head = _git.ResolveComparison(ComparisonMode.LastCommit, null, null).Sha!;
        var change = _git.GetChanges(head).Single(c => c.Path == "huge.log");

        Assert.Equal(ChangeKind.Untracked, change.Kind);   // still shown in the tree
        Assert.Equal(0, change.LinesAdded);                // but not counted
        Assert.False(change.IsBinary);
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
        // Clear read-only attributes that Git sets on objects under .git.
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
