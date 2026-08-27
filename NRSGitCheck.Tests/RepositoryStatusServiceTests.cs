using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using NRSGitCheck.Services;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Exercises the Repositories tab's status read against real temporary
/// repositories: branch, uncommitted work, unpushed commits, and the failure
/// modes it has to survive rather than throw on.
/// </summary>
public sealed class RepositoryStatusServiceTests : IDisposable
{
    private readonly string _root;
    private readonly RepositoryStatusService _service = new();

    public RepositoryStatusServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NRSGitCheckStatus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { DeleteDirectory(_root); } catch { /* best effort; LibGit2 may hold locks briefly */ }
    }

    [Fact]
    public void A_folder_that_is_not_a_repository_comes_back_invalid()
    {
        var dir = Path.Combine(_root, "plain");
        Directory.CreateDirectory(dir);

        var status = _service.Read(dir);

        Assert.False(status.IsValid);
        Assert.NotNull(status.Error);
    }

    [Fact]
    public void A_missing_folder_comes_back_invalid()
    {
        var status = _service.Read(Path.Combine(_root, "gone"));

        Assert.False(status.IsValid);
        Assert.Contains("missing", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_committed_repository_is_clean_and_reports_its_branch()
    {
        var dir = InitRepo("clean");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");

        var status = _service.Read(dir);

        Assert.True(status.IsValid);
        Assert.Equal("main", status.CurrentBranch);
        Assert.False(status.HasUncommittedChanges);
        Assert.False(status.HasUnpushedCommits);
        Assert.Equal("main", status.MainBranch);
        Assert.True(status.IsOnMainBranch);
    }

    [Fact]
    public void Modified_and_untracked_files_both_count_as_uncommitted()
    {
        var dir = InitRepo("dirty");
        Commit(dir, "a.txt", "one");

        File.WriteAllText(Path.Combine(dir, "a.txt"), "one, edited");
        File.WriteAllText(Path.Combine(dir, "new.txt"), "brand new");

        var status = _service.Read(dir);

        Assert.True(status.HasUncommittedChanges);
        Assert.Equal(2, status.UncommittedCount);
    }

    [Fact]
    public void Local_branches_are_listed_and_the_feature_branch_is_not_main()
    {
        var dir = InitRepo("branches");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");

        using (var repo = new Repository(dir))
            Commands.Checkout(repo, repo.CreateBranch("feature"));

        var status = _service.Read(dir);

        Assert.Equal("feature", status.CurrentBranch);
        Assert.Contains("main", status.LocalBranches);
        Assert.Contains("feature", status.LocalBranches);
        Assert.Equal("main", status.MainBranch);
        Assert.False(status.IsOnMainBranch);
    }

    [Fact]
    public void Master_is_recognized_as_main_when_there_is_no_main()
    {
        var dir = InitRepo("legacy");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "master");

        var status = _service.Read(dir);

        Assert.Equal("master", status.MainBranch);
        Assert.Equal("master", status.LocalMainBranch);
        Assert.True(status.IsOnMainBranch);
    }

    [Fact]
    public void A_commit_made_after_pushing_counts_as_unpushed()
    {
        var dir = InitRepo("pushed");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "main");
        PushToNewLocalRemote(dir, "main");

        Assert.False(_service.Read(dir).HasUnpushedCommits);

        Commit(dir, "b.txt", "two");
        var status = _service.Read(dir);

        Assert.True(status.HasUpstream);
        Assert.True(status.HasUnpushedCommits);
        Assert.Equal(1, status.AheadBy);
    }

    [Fact]
    public void A_repo_checked_out_on_master_counts_as_on_main_even_when_main_also_exists()
    {
        // Both branches present, master checked out. Detection alone prefers "main",
        // which would wrongly exclude this repo from the bulk pull.
        var dir = InitRepo("both");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "master");

        using (var repo = new Repository(dir))
            repo.CreateBranch("main");

        var status = _service.Read(dir);

        Assert.Equal("master", status.CurrentBranch);
        Assert.Equal("master", status.MainBranch);
        Assert.True(status.IsOnMainBranch);
    }

    [Fact]
    public void A_repo_on_a_feature_branch_still_falls_back_to_the_detected_main()
    {
        var dir = InitRepo("feature-side");
        Commit(dir, "a.txt", "one");
        RenameCurrentBranch(dir, "master");

        using (var repo = new Repository(dir))
            Commands.Checkout(repo, repo.CreateBranch("feature"));

        var status = _service.Read(dir);

        Assert.Equal("master", status.MainBranch);
        Assert.False(status.IsOnMainBranch);
    }

    [Fact]
    public async Task ReadAll_preserves_the_order_it_was_given()
    {
        var first = InitRepo("first");
        Commit(first, "a.txt", "one");
        var second = InitRepo("second");
        Commit(second, "a.txt", "one");

        var statuses = await _service.ReadAllAsync(new[] { first, second, Path.Combine(_root, "gone") });

        Assert.Equal(3, statuses.Count);
        Assert.Equal("first", statuses[0].Name);
        Assert.Equal("second", statuses[1].Name);
        Assert.False(statuses[2].IsValid);
    }

    // --- the changed-path list the commit / discard dialogs show -------------

    [Fact]
    public void Untracked_paths_are_listed_before_edited_ones()
    {
        var dir = InitRepo("ordering");
        Commit(dir, "a.txt", "one");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "edited");
        // Sorts last alphabetically, so only the untracked-first rule can lift it.
        File.WriteAllText(Path.Combine(dir, "zz-new.txt"), "new");

        var status = _service.Read(dir);

        Assert.Equal(2, status.UncommittedCount);
        Assert.Equal(1, status.UntrackedCount);
        Assert.Equal("zz-new.txt", status.Changes[0].Path);
        Assert.True(status.Changes[0].IsUntracked);
        Assert.Equal("a.txt", status.Changes[1].Path);
    }

    /// <summary>
    /// The whole point of the ordering: a discard deletes untracked files outright, so
    /// the 200-path cap must not be able to push them all out of the list behind a
    /// mountain of edited files.
    /// </summary>
    [Fact]
    public void The_cap_drops_edited_files_rather_than_untracked_ones()
    {
        var dir = InitRepo("capped");

        using (var repo = new Repository(dir))
        {
            for (var i = 0; i < 400; i++)
                File.WriteAllText(Path.Combine(dir, $"tracked{i:000}.txt"), "one");
            Commands.Stage(repo, "*");
            var sig = new Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("add many", sig, sig);
        }

        for (var i = 0; i < 400; i++)
            File.WriteAllText(Path.Combine(dir, $"tracked{i:000}.txt"), "edited");

        // "z…" sorts after every tracked path, so alphabetical order alone would put
        // these last and the cap would cut every one of them.
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(dir, $"zz-untracked{i}.txt"), "new");

        var status = _service.Read(dir);

        Assert.Equal(405, status.UncommittedCount);
        Assert.Equal(5, status.UntrackedCount);
        Assert.True(status.HasUnlistedChanges);

        var listed = status.Changes.Where(c => c.IsUntracked).Select(c => c.Path).ToList();
        Assert.Equal(5, listed.Count);
        Assert.All(listed, path => Assert.StartsWith("zz-untracked", path));
    }

    [Fact]
    public void A_clean_repository_lists_no_changes()
    {
        var dir = InitRepo("nochanges");
        Commit(dir, "a.txt", "one");

        var status = _service.Read(dir);

        Assert.Empty(status.Changes);
        Assert.Equal(0, status.UntrackedCount);
        Assert.False(status.HasUnlistedChanges);
    }

    [Fact]
    public void A_deleted_file_is_listed_as_deleted()
    {
        var dir = InitRepo("deleted");
        Commit(dir, "a.txt", "one");
        File.Delete(Path.Combine(dir, "a.txt"));

        var status = _service.Read(dir);

        var change = Assert.Single(status.Changes);
        Assert.Equal("a.txt", change.Path);
        Assert.Equal(NRSGitCheck.Models.ChangeKind.Deleted, change.Kind);
        Assert.Equal(0, status.UntrackedCount);
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

    /// <summary>Gives the branch an upstream by pushing it to a bare repo on disk.</summary>
    private void PushToNewLocalRemote(string dir, string branch)
    {
        var remotePath = Path.Combine(_root, Path.GetFileName(dir) + ".git");
        Repository.Init(remotePath, isBare: true);

        using var repo = new Repository(dir);
        repo.Network.Remotes.Add("origin", remotePath);

        var local = repo.Branches[branch];
        repo.Branches.Update(local,
            b => b.Remote = "origin",
            b => b.UpstreamBranch = local.CanonicalName);

        repo.Network.Push(local, new PushOptions());
    }

    private static void DeleteDirectory(string path)
    {
        // Clear read-only attributes that Git sets on objects under .git.
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
