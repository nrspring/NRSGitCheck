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
/// File-to-file keyboard navigation: it clamps at both ends instead of wrapping,
/// and reports whether the selection actually moved so callers can distinguish a
/// real step from a no-op at the boundary.
/// </summary>
public sealed class FileNavigationTests : IDisposable
{
    private readonly string _dir;
    private readonly MainWindowViewModel _vm;

    public FileNavigationTests()
    {
        // OpenPathAsync only reaches the stub git service if the recent entry's
        // directory really exists, so back it with a temp folder.
        _dir = Path.Combine(Path.GetTempPath(), "NRSGitCheckNav", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var settings = new StubSettings();
        settings.Settings.ReopenLastRepoOnLaunch = true;
        settings.Settings.RecentRepositories.Add(new RecentRepository { Path = _dir, Name = "repo" });

        _vm = new MainWindowViewModel(
            settings,
            new StubGit(_dir, "a.txt", "b.txt", "c.txt"),
            new StubGitCommands(),
            new StubFolderPicker(),
            new DiffViewModel(new StubDiff(), settings, new StubClipboard()),
            new StubTheme(),
            Repositories(settings));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<MainWindowViewModel> Loaded()
    {
        await _vm.InitializeAsync();
        return _vm;
    }

    [Fact]
    public async Task NextFile_stops_at_the_last_file_instead_of_wrapping()
    {
        var vm = await Loaded();

        Assert.True(vm.NextFile());   // -> a.txt
        Assert.True(vm.NextFile());   // -> b.txt
        Assert.True(vm.NextFile());   // -> c.txt
        Assert.Equal("c.txt", vm.SelectedFile?.Path);

        // At the end: no move, and crucially not back round to a.txt.
        Assert.False(vm.NextFile());
        Assert.Equal("c.txt", vm.SelectedFile?.Path);
    }

    [Fact]
    public async Task PreviousFile_stops_at_the_first_file_instead_of_wrapping()
    {
        var vm = await Loaded();

        Assert.True(vm.PreviousFile());  // nothing selected -> last file
        Assert.Equal("c.txt", vm.SelectedFile?.Path);

        Assert.True(vm.PreviousFile());  // -> b.txt
        Assert.True(vm.PreviousFile());  // -> a.txt
        Assert.Equal("a.txt", vm.SelectedFile?.Path);

        Assert.False(vm.PreviousFile());
        Assert.Equal("a.txt", vm.SelectedFile?.Path);
    }

    [Fact]
    public async Task Navigation_reports_no_move_when_there_are_no_files()
    {
        var settings = new StubSettings();
        var vm = new MainWindowViewModel(
            settings,
            new StubGit(_dir),                 // no changed files
            new StubGitCommands(),
            new StubFolderPicker(),
            new DiffViewModel(new StubDiff(), settings, new StubClipboard()),
            new StubTheme(),
            Repositories(settings));

        await vm.InitializeAsync();

        Assert.False(vm.NextFile());
        Assert.False(vm.PreviousFile());
        Assert.Null(vm.SelectedFile);
    }

    // --- stubs --------------------------------------------------------------

    /// <summary>The Repositories tab is inert in these tests; it just has to exist.</summary>
    private static RepositoriesViewModel Repositories(ISettingsService settings) =>
        new(settings, new StubRepositoryStatus(), new StubGitCommands(), new StubFolderPicker(),
            new RoslynExpressionEvaluator(), new StubClipboard());

    private sealed class StubRepositoryStatus : IRepositoryStatusService
    {
        public NRSGitCheck.Models.RepositoryStatus Read(string path) =>
            NRSGitCheck.Models.RepositoryStatus.Failed(path, "stub", "not read");

        public Task<IReadOnlyList<NRSGitCheck.Models.RepositoryStatus>> ReadAllAsync(
            IEnumerable<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NRSGitCheck.Models.RepositoryStatus>>(
                paths.Select(Read).ToList());
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

    private sealed class StubGit : IGitService
    {
        private readonly string _dir;
        private readonly string[] _paths;

        public StubGit(string dir, params string[] paths)
        {
            _dir = dir;
            _paths = paths;
        }

        public RepositorySnapshot OpenRepository(string path) => new(
            _dir, "repo", "feature", false, false, "abc1234",
            new[] { new BranchInfo("main", "sha", "sha", false) }, "main", "main", false,
            "https://github.com/owner/repo.git");

        public ResolvedComparison ResolveComparison(
            ComparisonMode mode, string? otherBranch, string? parentBranch, string? commitSha = null) =>
            ResolvedComparison.Resolved("basesha", "last commit (basesha)");

        public IReadOnlyList<CommitInfo> GetBranchCommits(string? mainBranch, int maxCount = 200) =>
            Array.Empty<CommitInfo>();

        public IReadOnlyList<FileChange> GetChanges(string baseCommitSha)
        {
            var list = new List<FileChange>();
            foreach (var p in _paths)
                list.Add(new FileChange(p, null, ChangeKind.Modified, 1, 1, false));
            return list;
        }

        public int GetUncommittedChangeCount() => 0;

        public IReadOnlyDictionary<string, FileStats> GetChangeStats(string baseCommitSha) =>
            new Dictionary<string, FileStats>();

        public FileContent GetFileContent(string baseCommitSha, FileChange change) =>
            new("old", "new", false);
    }

    private sealed class StubGitCommands : IGitCommandService
    {
        public Task<GitCommandResult> PullMainAsync(
            string workingDirectory, string? mainBranch, string? currentBranch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, "up to date"));

        public Task<GitCommandResult> CheckoutPullRequestAsync(
            string workingDirectory, PullRequestReference pr, string? currentBranch,
            CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"checked out pr-{pr.Number}"));

        public Task<GitCommandResult> CheckoutBranchAsync(
            string workingDirectory, string branch, CancellationToken ct = default) =>
            Task.FromResult(new GitCommandResult(true, $"checked out {branch}"));

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

    /// <summary>Serves a diff with three well-separated hunks for every file.</summary>
    private sealed class StubMultiHunkDiff : IDiffService
    {
        public DiffStream BuildDiffStream(
            string baseCommitSha, FileChange change, int contextLines = 3, bool wholeFile = false)
        {
            var oldText = new System.Text.StringBuilder();
            var newText = new System.Text.StringBuilder();
            for (var i = 0; i < 90; i++)
            {
                var changed = i is 10 or 40 or 70;   // three edits, far apart -> three hunks
                oldText.Append("line").Append(i).Append('\n');
                newText.Append(changed ? "CHANGED" : "line").Append(i).Append('\n');
            }

            var doc = DiffEngine.Compute(oldText.ToString(), newText.ToString(), contextLines);
            return new DiffStream { Hunks = doc.Hunks };
        }
    }

    [Fact]
    public async Task NextHunk_walks_the_file_then_crosses_into_the_next_file()
    {
        var settings = new StubSettings();
        settings.Settings.ReopenLastRepoOnLaunch = true;
        settings.Settings.RecentRepositories.Add(new RecentRepository { Path = _dir, Name = "repo" });

        var diff = new DiffViewModel(new StubMultiHunkDiff(), settings, new StubClipboard());
        var vm = new MainWindowViewModel(
            settings, new StubGit(_dir, "a.txt", "b.txt"), new StubGitCommands(),
            new StubFolderPicker(), diff, new StubTheme(), Repositories(settings));

        await vm.InitializeAsync();

        vm.NextFile();                                  // select a.txt
        await Settled(diff);
        Assert.Equal("a.txt", vm.SelectedFile?.Path);
        Assert.Equal(3, diff.SectionCount);
        Assert.Equal(0, diff.CurrentSectionIndex);

        vm.NextChange();
        Assert.Equal(1, diff.CurrentSectionIndex);
        Assert.Equal("a.txt", vm.SelectedFile?.Path);   // still in the same file

        vm.NextChange();
        Assert.Equal(2, diff.CurrentSectionIndex);
        Assert.Equal("a.txt", vm.SelectedFile?.Path);

        vm.NextChange();                                  // out of hunks -> next file
        await Settled(diff);
        Assert.Equal("b.txt", vm.SelectedFile?.Path);
        Assert.Equal(0, diff.CurrentSectionIndex);         // lands on its FIRST change
    }

    [Fact]
    public async Task Pressing_next_again_mid_load_does_not_skip_the_incoming_file()
    {
        var (vm, diff) = MultiHunkRepo("a.txt", "b.txt", "c.txt");
        await vm.InitializeAsync();

        vm.NextFile();
        await Settled(diff);
        vm.NextChange();
        vm.NextChange();                 // now parked on a.txt's last hunk
        Assert.Equal("a.txt", vm.SelectedFile?.Path);

        vm.NextChange();                 // crosses into b.txt; its load is still in flight
        Assert.Equal("b.txt", vm.SelectedFile?.Path);
        Assert.True(diff.IsLoading);

        // The impatient second press must not be read as "b.txt has no more hunks".
        vm.NextChange();
        Assert.Equal("b.txt", vm.SelectedFile?.Path);

        await Settled(diff);
        Assert.Equal(0, diff.CurrentSectionIndex);   // sitting on b.txt's first change
        Assert.Equal(3, diff.SectionCount);
    }

    [Fact]
    public async Task Pressing_previous_again_mid_load_does_not_skip_the_incoming_file()
    {
        var (vm, diff) = MultiHunkRepo("a.txt", "b.txt", "c.txt");
        await vm.InitializeAsync();

        vm.PreviousFile();             // nothing selected -> c.txt, parked on its first hunk
        await Settled(diff);
        Assert.Equal("c.txt", vm.SelectedFile?.Path);
        Assert.Equal(0, diff.CurrentSectionIndex);

        vm.PreviousChange();             // crosses back into b.txt, load in flight
        Assert.Equal("b.txt", vm.SelectedFile?.Path);
        Assert.True(diff.IsLoading);

        vm.PreviousChange();
        Assert.Equal("b.txt", vm.SelectedFile?.Path);

        await Settled(diff);
        Assert.Equal(2, diff.CurrentSectionIndex);   // b.txt's last change
    }

    private (MainWindowViewModel, DiffViewModel) MultiHunkRepo(params string[] files)
    {
        var settings = new StubSettings();
        settings.Settings.ReopenLastRepoOnLaunch = true;
        settings.Settings.RecentRepositories.Add(new RecentRepository { Path = _dir, Name = "repo" });

        var diff = new DiffViewModel(new StubMultiHunkDiff(), settings, new StubClipboard());
        var vm = new MainWindowViewModel(
            settings, new StubGit(_dir, files), new StubGitCommands(),
            new StubFolderPicker(), diff, new StubTheme(), Repositories(settings));
        return (vm, diff);
    }

    /// <summary>Waits for the fire-and-forget diff load kicked off by selecting a file.</summary>
    private static async Task Settled(DiffViewModel diff)
    {
        for (var i = 0; i < 400 && diff.IsLoading; i++)
            await Task.Delay(5);
        Assert.False(diff.IsLoading, "diff load did not finish");
    }
}
