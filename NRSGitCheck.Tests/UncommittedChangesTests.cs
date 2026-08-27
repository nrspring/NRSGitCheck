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
/// The commit and discard flows behind the Repositories tab's uncommitted-changes
/// pill. The point of these is that nothing touches a repository until the dialog
/// is confirmed, and that a discard only deletes untracked files when asked to.
/// </summary>
public sealed class UncommittedChangesTests
{
    // --- Test doubles -------------------------------------------------------

    private sealed class RecordingGitCommands : IGitCommandService
    {
        public List<(string Path, string Message)> Commits { get; } = new();
        public List<(string Path, bool DeleteUntracked)> Discards { get; } = new();

        /// <summary>Set to make the next commit come back as a refusal from Git.</summary>
        public string? CommitRefusal { get; set; }

        /// <summary>Set to make the next discard come back as a refusal from Git.</summary>
        public string? DiscardRefusal { get; set; }

        public Task<GitCommandResult> CommitAllAsync(
            string workingDirectory, string message, CancellationToken ct = default)
        {
            Commits.Add((workingDirectory, message));
            return Task.FromResult(CommitRefusal is { } refusal
                ? new GitCommandResult(false, refusal)
                : new GitCommandResult(true, "[main 1a2b3c4] " + message));
        }

        public Task<GitCommandResult> DiscardChangesAsync(
            string workingDirectory, bool deleteUntrackedFiles, CancellationToken ct = default)
        {
            Discards.Add((workingDirectory, deleteUntrackedFiles));
            return Task.FromResult(DiscardRefusal is { } refusal
                ? new GitCommandResult(false, refusal)
                : new GitCommandResult(true, "Reverted tracked files."));
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
    }

    /// <summary>Hands back whatever status the test set, so a refresh is deterministic.</summary>
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

    // --- Fixtures -----------------------------------------------------------

    private static RepositoryStatus Status(
        int uncommitted = 3, int untracked = 1, IReadOnlyList<WorkingTreeChange>? changes = null) => new(
        Path: @"C:\repo", Name: "repo", IsValid: true, Error: null, CurrentBranch: "feature",
        IsDetachedHead: false, IsHeadUnborn: false, LocalBranches: new[] { "feature", "main" },
        MainBranch: "main", UncommittedCount: uncommitted, HasUpstream: true, AheadBy: 0, BehindBy: 0,
        HasRemote: true,
        Changes: changes ?? new[]
        {
            new WorkingTreeChange("src/a.cs", ChangeKind.Modified),
            new WorkingTreeChange("src/b.cs", ChangeKind.Deleted),
            new WorkingTreeChange("scratch.txt", ChangeKind.Untracked),
        },
        UntrackedCount: untracked);

    private sealed class Fixture
    {
        public RecordingGitCommands Git { get; } = new();
        public StubStatusService Statuses { get; } = new();
        public RepositoriesViewModel Owner { get; }
        public TrackedRepositoryViewModel Row { get; }

        public Fixture(RepositoryStatus? initial = null)
        {
            var settings = new StubSettings();
            settings.Settings.TrackedRepositories.Add(new TrackedRepository { Path = @"C:\repo", Name = "repo" });

            Owner = new RepositoriesViewModel(
                settings, Statuses, Git, new StubFolderPicker(), new StubEvaluator(), new StubClipboard());

            Row = Owner.Repositories.Single();
            Row.Apply(initial ?? Status());
        }
    }

    // --- The pill's two commands --------------------------------------------

    [Fact]
    public void Both_actions_are_offered_only_while_there_is_uncommitted_work()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Row.CommitChangesCommand.CanExecute(null));
        Assert.True(fixture.Row.DiscardChangesCommand.CanExecute(null));

        fixture.Row.Apply(Status(uncommitted: 0, untracked: 0, changes: Array.Empty<WorkingTreeChange>()));

        Assert.False(fixture.Row.CommitChangesCommand.CanExecute(null));
        Assert.False(fixture.Row.DiscardChangesCommand.CanExecute(null));
    }

    [Fact]
    public void Commit_action_opens_the_dialog_without_running_anything()
    {
        var fixture = new Fixture();

        fixture.Row.CommitChangesCommand.Execute(null);

        Assert.True(fixture.Owner.Commit.IsVisible);
        Assert.Equal("repo", fixture.Owner.Commit.RepositoryName);
        Assert.Equal("feature", fixture.Owner.Commit.Branch);
        Assert.Equal(3, fixture.Owner.Commit.Changes.Count);
        Assert.Empty(fixture.Git.Commits);
    }

    [Fact]
    public void Discard_action_opens_the_confirmation_without_running_anything()
    {
        var fixture = new Fixture();

        fixture.Row.DiscardChangesCommand.Execute(null);

        Assert.True(fixture.Owner.Discard.IsVisible);
        Assert.Empty(fixture.Git.Discards);
    }

    // --- Commit -------------------------------------------------------------

    [Fact]
    public void Commit_is_refused_until_a_message_is_typed()
    {
        var fixture = new Fixture();
        var dialog = fixture.Owner.Commit;
        dialog.Open(fixture.Row);

        Assert.False(dialog.CommitCommand.CanExecute(null));

        dialog.Message = "   ";
        Assert.False(dialog.CommitCommand.CanExecute(null));

        dialog.Message = "Fix the thing";
        Assert.True(dialog.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Committing_passes_the_message_through_and_closes_the_dialog()
    {
        var fixture = new Fixture();
        var dialog = fixture.Owner.Commit;
        dialog.Open(fixture.Row);
        dialog.Message = "Fix the thing";

        fixture.Statuses.Next = Status(uncommitted: 0, untracked: 0, changes: Array.Empty<WorkingTreeChange>());
        await dialog.CommitCommand.ExecuteAsync(null);

        var commit = Assert.Single(fixture.Git.Commits);
        Assert.Equal(@"C:\repo", commit.Path);
        Assert.Equal("Fix the thing", commit.Message);
        Assert.False(dialog.IsVisible);
        Assert.Null(dialog.Error);

        // The row was re-read, so the pill is gone.
        Assert.False(fixture.Row.HasUncommittedChanges);
    }

    [Fact]
    public async Task A_refused_commit_keeps_the_dialog_and_the_message_open()
    {
        var fixture = new Fixture();
        fixture.Git.CommitRefusal = "Author identity unknown";

        var dialog = fixture.Owner.Commit;
        dialog.Open(fixture.Row);
        dialog.Message = "Fix the thing";

        await dialog.CommitCommand.ExecuteAsync(null);

        Assert.True(dialog.IsVisible);
        Assert.Equal("Fix the thing", dialog.Message);
        Assert.Equal("Author identity unknown", dialog.Error);
        Assert.Contains("Author identity unknown", fixture.Owner.ErrorMessage);
    }

    [Fact]
    public void Cancelling_the_commit_dialog_runs_nothing()
    {
        var fixture = new Fixture();
        var dialog = fixture.Owner.Commit;
        dialog.Open(fixture.Row);
        dialog.Message = "Fix the thing";

        dialog.CancelCommand.Execute(null);

        Assert.False(dialog.IsVisible);
        Assert.Empty(fixture.Git.Commits);
    }

    [Fact]
    public void The_commit_dialog_reports_the_exact_count_even_when_the_list_was_capped()
    {
        var fixture = new Fixture(Status(uncommitted: 250, untracked: 0));
        var dialog = fixture.Owner.Commit;

        dialog.Open(fixture.Row);

        Assert.Equal("250 changes will be committed", dialog.Summary);
        Assert.True(dialog.HasUnlistedChanges);
        Assert.Equal("…and 247 more", dialog.UnlistedChangesText);
    }

    // --- Discard ------------------------------------------------------------

    [Fact]
    public async Task Discarding_leaves_untracked_files_alone_by_default()
    {
        var fixture = new Fixture();
        var dialog = fixture.Owner.Discard;
        dialog.Open(fixture.Row);

        Assert.False(dialog.DeleteUntrackedFiles);

        fixture.Statuses.Next = Status(uncommitted: 1, untracked: 1);
        await dialog.DiscardCommand.ExecuteAsync(null);

        var discard = Assert.Single(fixture.Git.Discards);
        Assert.Equal(@"C:\repo", discard.Path);
        Assert.False(discard.DeleteUntracked);
        Assert.False(dialog.IsVisible);
    }

    [Fact]
    public async Task Untracked_files_are_deleted_only_when_the_box_is_ticked()
    {
        var fixture = new Fixture();
        var dialog = fixture.Owner.Discard;
        dialog.Open(fixture.Row);
        dialog.DeleteUntrackedFiles = true;

        fixture.Statuses.Next = Status(uncommitted: 0, untracked: 0, changes: Array.Empty<WorkingTreeChange>());
        await dialog.DiscardCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(fixture.Git.Discards).DeleteUntracked);
    }

    [Fact]
    public void Reopening_the_confirmation_forgets_a_previous_yes()
    {
        var fixture = new Fixture();
        var dialog = fixture.Owner.Discard;

        dialog.Open(fixture.Row);
        dialog.DeleteUntrackedFiles = true;
        dialog.CancelCommand.Execute(null);

        dialog.Open(fixture.Row);

        Assert.False(dialog.DeleteUntrackedFiles);
    }

    /// <summary>
    /// The button binds to the command's cached answer, so opening has to re-ask —
    /// otherwise the confirmation comes up with Discard greyed out.
    /// </summary>
    [Fact]
    public void Opening_the_confirmation_re_enables_the_discard_button()
    {
        var fixture = new Fixture();
        var dialog = fixture.Owner.Discard;
        dialog.Open(fixture.Row);
        dialog.CancelCommand.Execute(null);

        var raised = false;
        dialog.DiscardCommand.CanExecuteChanged += (_, _) => raised = true;

        dialog.Open(fixture.Row);

        Assert.True(raised);
        Assert.True(dialog.DiscardCommand.CanExecute(null));
    }

    [Fact]
    public void Opening_the_commit_dialog_re_asks_whether_commit_is_available()
    {
        var fixture = new Fixture();
        var dialog = fixture.Owner.Commit;
        dialog.Open(fixture.Row);
        dialog.CancelCommand.Execute(null);

        var raised = false;
        dialog.CommitCommand.CanExecuteChanged += (_, _) => raised = true;

        dialog.Open(fixture.Row);

        Assert.True(raised);
    }

    [Fact]
    public void Cancelling_the_confirmation_discards_nothing()
    {
        var fixture = new Fixture();
        var dialog = fixture.Owner.Discard;
        dialog.Open(fixture.Row);

        dialog.CancelCommand.Execute(null);

        Assert.False(dialog.IsVisible);
        Assert.Empty(fixture.Git.Discards);
    }

    [Fact]
    public void The_confirmation_separates_tracked_files_from_untracked_ones()
    {
        var fixture = new Fixture(Status(uncommitted: 10, untracked: 4));
        var dialog = fixture.Owner.Discard;

        dialog.Open(fixture.Row);

        Assert.Equal("6 tracked files will be put back to their committed state", dialog.TrackedSummary);
        Assert.True(dialog.HasUntrackedFiles);
        Assert.Equal("Also delete 4 untracked files", dialog.UntrackedSummary);
    }

    /// <summary>
    /// The status read puts untracked paths first so the cap cannot hide them; the
    /// dialog must show them in that order rather than re-sorting the sample.
    /// </summary>
    [Fact]
    public void The_confirmation_lists_untracked_paths_first()
    {
        var changes = new[]
        {
            new WorkingTreeChange("zz-new.txt", ChangeKind.Untracked),
            new WorkingTreeChange("src/a.cs", ChangeKind.Modified),
            new WorkingTreeChange("src/b.cs", ChangeKind.Deleted),
        };
        var fixture = new Fixture(Status(uncommitted: 3, untracked: 1, changes: changes));
        var dialog = fixture.Owner.Discard;

        dialog.Open(fixture.Row);

        Assert.Equal("zz-new.txt", dialog.Changes[0].Path);
        Assert.True(dialog.Changes[0].IsUntracked);
    }

    [Fact]
    public void The_untracked_opt_in_is_hidden_when_there_are_none()
    {
        var fixture = new Fixture(Status(uncommitted: 2, untracked: 0));
        var dialog = fixture.Owner.Discard;

        dialog.Open(fixture.Row);

        Assert.False(dialog.HasUntrackedFiles);
    }

    [Fact]
    public async Task A_refused_discard_keeps_the_confirmation_open()
    {
        var fixture = new Fixture();
        fixture.Git.DiscardRefusal = "unable to unlink src/a.cs";

        var dialog = fixture.Owner.Discard;
        dialog.Open(fixture.Row);

        await dialog.DiscardCommand.ExecuteAsync(null);

        Assert.True(dialog.IsVisible);
        Assert.Equal("unable to unlink src/a.cs", dialog.Error);
        Assert.Contains("unable to unlink", fixture.Owner.ErrorMessage);
    }
}
