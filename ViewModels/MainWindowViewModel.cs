using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NRSGitCheck.Models;
using NRSGitCheck.Services;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// View model for the single application window. Owns the open repository, the
/// recent-repo history, and the comparison-target selection. The changed-file
/// list and diff (Phase 3+) plug into <see cref="RefreshComparisonAsync"/>.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IGitService _git;
    private readonly IGitCommandService _gitCommands;
    private readonly IFolderPickerService _folderPicker;

    private readonly IThemeService _themeService;

    public MainWindowViewModel(
        ISettingsService settings,
        IGitService git,
        IGitCommandService gitCommands,
        IFolderPickerService folderPicker,
        DiffViewModel diff,
        IThemeService themeService,
        RepositoriesViewModel repositories)
    {
        _settings = settings;
        _git = git;
        _gitCommands = gitCommands;
        _folderPicker = folderPicker;
        _themeService = themeService;
        Diff = diff;
        Repositories = repositories;

        _selectedMode = ComparisonModes.FirstOrDefault(o => o.Mode == settings.Settings.LastComparisonMode)
                        ?? ComparisonModes[0];
        _selectedTheme = ThemeModes.FirstOrDefault(o => o.Mode == settings.Settings.ThemeMode)
                        ?? ThemeModes[0];
        _autoRefreshEnabled = settings.Settings.AutoRefreshEnabled;

        // Re-render the open diff when the effective theme changes so syntax
        // colors switch with it (FR-20, FR-28).
        _themeService.EffectiveThemeChanged += OnEffectiveThemeChanged;
    }

    // --- Theme selection ----------------------------------------------------

    public IReadOnlyList<ThemeOption> ThemeModes { get; } = new[]
    {
        new ThemeOption(ThemeMode.System, "System"),
        new ThemeOption(ThemeMode.Light, "Light"),
        new ThemeOption(ThemeMode.Dark, "Dark"),
    };

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    partial void OnSelectedThemeChanged(ThemeOption value) => _themeService.SetMode(value.Mode);

    private void OnEffectiveThemeChanged()
    {
        if (SelectedFile is { } file && _currentBaseSha is { } sha)
            _ = Diff.LoadAsync(sha, file.Model);
    }

    /// <summary>The diff view model for the selected file.</summary>
    public DiffViewModel Diff { get; }

    /// <summary>The Repositories tab: the user's pinned repositories and their state.</summary>
    public RepositoriesViewModel Repositories { get; }

    /// <summary>Index of the tab on screen: 0 = Review, 1 = Repositories.</summary>
    private const int RepositoriesTabIndex = 1;

    /// <summary>Which top-level tab is showing.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    partial void OnSelectedTabIndexChanged(int value)
    {
        // Reading every tracked repository costs a Git handle each, so the tab pays
        // for its first sweep when it is opened rather than at launch.
        if (value == RepositoriesTabIndex)
            _ = Repositories.EnsureLoadedAsync();
    }

    /// <summary>The resolved base commit SHA for the current comparison, if any.</summary>
    private string? _currentBaseSha;

    // --- Shell chrome -------------------------------------------------------

    [ObservableProperty]
    private string _title = "NRSGitCheck";

    [ObservableProperty]
    private string _status = "Open a repository to begin.";

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => PullMainCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Whether the repo-picker ribbon (recent pills + open folder) is shown.</summary>
    [ObservableProperty]
    private bool _isRepoRibbonVisible;

    // --- Repository state ---------------------------------------------------

    [ObservableProperty]
    private bool _hasRepo;

    partial void OnHasRepoChanged(bool value)
    {
        PullMainCommand.NotifyCanExecuteChanged();
        OpenPullRequestDialogCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    private string? _repositoryName;

    [ObservableProperty]
    private string? _currentBranch;

    [ObservableProperty]
    private string? _headShortSha;

    [ObservableProperty]
    private string _resolvedTargetLabel = string.Empty;

    /// <summary>Working directory of the open repo; needed by the Git CLI for Pull main.</summary>
    private string? _workingDirectory;

    /// <summary>Fetch URL of the repository's origin, used to sanity-check pasted PR links.</summary>
    private string? _originUrl;

    /// <summary>The detected integration branch ("main", "master", or "origin/main").</summary>
    [ObservableProperty]
    private string? _mainBranchName;

    /// <summary>Whether the repo has any remote — Pull main is pointless without one.</summary>
    [ObservableProperty]
    private bool _hasRemote;

    public ObservableCollection<RecentRepositoryViewModel> RecentRepositories { get; } = new();
    public ObservableCollection<string> LocalBranches { get; } = new();

    [ObservableProperty]
    private bool _hasRecentRepositories;

    // --- Changed-files list -------------------------------------------------

    private List<FileChangeViewModel> _allFiles = new();

    /// <summary>Root of the folder/file tree bound to the changed-files view.</summary>
    public ObservableCollection<FileTreeNode> RootNodes { get; } = new();

    /// <summary>File leaves in visual (depth-first) order, for keyboard navigation.</summary>
    private readonly List<FileNode> _orderedFileNodes = new();

    /// <summary>The node selected in the tree (folder or file).</summary>
    [ObservableProperty]
    private FileTreeNode? _selectedNode;

    /// <summary>The currently shown file; drives the diff pane.</summary>
    [ObservableProperty]
    private FileChangeViewModel? _selectedFile;

    [ObservableProperty]
    private string? _fileFilter;

    [ObservableProperty]
    private string _changedFilesSummary = string.Empty;

    partial void OnFileFilterChanged(string? value) => ApplyFilter();

    // --- Comparison target --------------------------------------------------

    public IReadOnlyList<ComparisonModeOption> ComparisonModes { get; } = new[]
    {
        new ComparisonModeOption(ComparisonMode.LastCommit, "Uncommitted changes"),
        new ComparisonModeOption(ComparisonMode.SinceCommit, "Since commit…"),
        new ComparisonModeOption(ComparisonMode.VsMain, "All changes vs main"),
        new ComparisonModeOption(ComparisonMode.OtherBranch, "Another branch"),
        new ComparisonModeOption(ComparisonMode.BranchBase, "Branch base (merge-base)"),
    };

    [ObservableProperty]
    private ComparisonModeOption _selectedMode;

    [ObservableProperty]
    private string? _selectedBranch;

    [ObservableProperty]
    private string? _parentBranch;

    /// <summary>Commits on the current branch, newest first, back to the branch point.</summary>
    public ObservableCollection<CommitInfo> BranchCommits { get; } = new();

    /// <summary>The commit the working tree is compared against in <see cref="ComparisonMode.SinceCommit"/>.</summary>
    [ObservableProperty]
    private CommitInfo? _selectedCommit;

    public bool IsOtherBranchMode => SelectedMode?.Mode == ComparisonMode.OtherBranch;
    public bool IsBranchBaseMode => SelectedMode?.Mode == ComparisonMode.BranchBase;
    public bool IsSinceCommitMode => SelectedMode?.Mode == ComparisonMode.SinceCommit;

    partial void OnSelectedModeChanged(ComparisonModeOption value)
    {
        OnPropertyChanged(nameof(IsOtherBranchMode));
        OnPropertyChanged(nameof(IsBranchBaseMode));
        OnPropertyChanged(nameof(IsSinceCommitMode));

        // Entering the commit picker with nothing chosen defaults to the branch point,
        // which shows the whole branch; the user can then step forward through commits.
        if (value?.Mode == ComparisonMode.SinceCommit && SelectedCommit is null && BranchCommits.Count > 0)
        {
            SetField(() => SelectedCommit = BranchCommits[^1]);
        }

        TriggerRefresh();
    }

    partial void OnSelectedBranchChanged(string? value) => TriggerRefresh();
    partial void OnParentBranchChanged(string? value) => TriggerRefresh();
    partial void OnSelectedCommitChanged(CommitInfo? value) => TriggerRefresh();

    // --- Lifecycle ----------------------------------------------------------

    /// <summary>
    /// Called once after the window opens: populates recent repos and optionally
    /// reopens the last repository (FR-6).
    /// </summary>
    public async Task InitializeAsync()
    {
        ReloadRecentRepositories();

        if (_settings.Settings.ReopenLastRepoOnLaunch)
        {
            var last = _settings.Settings.RecentRepositories.FirstOrDefault(r => r.DirectoryExists);
            if (last is not null)
                await OpenPathAsync(last.Path);
        }
    }

    // --- Commands -----------------------------------------------------------

    [RelayCommand]
    private async Task OpenRepository()
    {
        var path = await _folderPicker.PickFolderAsync("Select a Git repository");
        if (!string.IsNullOrEmpty(path))
            await OpenPathAsync(path);
    }

    [RelayCommand]
    private Task Refresh() => RefreshComparisonAsync();

    // --- Review a pull request ----------------------------------------------

    /// <summary>Whether the "review a pull request" modal is showing.</summary>
    [ObservableProperty]
    private bool _isPullRequestDialogVisible;

    /// <summary>The link or number the user pasted.</summary>
    [ObservableProperty]
    private string? _pullRequestInput;

    /// <summary>Validation or Git failure shown inside the modal.</summary>
    [ObservableProperty]
    private string? _pullRequestError;

    /// <summary>True while the fetch/checkout is running, to disable the modal's buttons.</summary>
    [ObservableProperty]
    private bool _isFetchingPullRequest;

    partial void OnIsFetchingPullRequestChanged(bool value) =>
        CheckoutPullRequestCommand.NotifyCanExecuteChanged();

    partial void OnPullRequestInputChanged(string? value) =>
        PullRequestError = null; // stop showing an error about text they've since edited

    private bool CanOpenPullRequestDialog() => HasRepo && HasRemote;

    [RelayCommand(CanExecute = nameof(CanOpenPullRequestDialog))]
    private void OpenPullRequestDialog()
    {
        PullRequestError = null;
        PullRequestInput = null;
        IsPullRequestDialogVisible = true;
        PullRequestInputRequested?.Invoke();
    }

    [RelayCommand]
    private void ClosePullRequestDialog() => IsPullRequestDialogVisible = false;

    /// <summary>Asks the view to focus the modal's text box when it opens.</summary>
    public event Action? PullRequestInputRequested;

    private bool CanCheckoutPullRequest() => !IsFetchingPullRequest;

    /// <summary>
    /// Fetches the pasted pull request onto a local branch, checks it out, and
    /// switches the comparison to "all changes vs main" so the diff matches the
    /// PR's own Files-changed view.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckoutPullRequest))]
    private async Task CheckoutPullRequest()
    {
        if (_workingDirectory is null)
            return;

        if (!PullRequestReference.TryParse(PullRequestInput, out var pr, out var parseError) || pr is null)
        {
            PullRequestError = parseError;
            return;
        }

        // A link from another project would silently fetch that project's PR number
        // into this repository, so refuse rather than review the wrong thing.
        var originSlug = PullRequestReference.SlugFromRemoteUrl(_originUrl);
        if (pr.Slug is { } linkSlug && originSlug is not null &&
            !string.Equals(linkSlug, originSlug, StringComparison.OrdinalIgnoreCase))
        {
            PullRequestError =
                $"That link is for {linkSlug}, but this repository is {originSlug}.";
            return;
        }

        PullRequestError = null;
        IsFetchingPullRequest = true;
        var previousStatus = Status;
        Status = $"Fetching PR #{pr.Number}…";
        try
        {
            var result = await _gitCommands.CheckoutPullRequestAsync(_workingDirectory, pr, CurrentBranch);
            if (!result.Success)
            {
                PullRequestError = result.Message;
                Status = previousStatus;
                return;
            }

            // HEAD and the working tree both moved; reopen so the rest of the app
            // sees the new branch rather than the cached one.
            var snapshot = await Task.Run(() => _git.OpenRepository(_workingDirectory));
            ApplySnapshot(snapshot);

            // The PR's own diff is measured from where it forked off main.
            SetField(() => SelectedMode =
                ComparisonModes.FirstOrDefault(m => m.Mode == ComparisonMode.VsMain) ?? SelectedMode);
            OnPropertyChanged(nameof(IsOtherBranchMode));
            OnPropertyChanged(nameof(IsBranchBaseMode));
            OnPropertyChanged(nameof(IsSinceCommitMode));

            IsPullRequestDialogVisible = false;
            await RefreshComparisonAsync();
            Status = result.Message;
        }
        catch (GitException ex)
        {
            PullRequestError = ex.Message;
        }
        catch (Exception ex)
        {
            PullRequestError = $"Could not check out the pull request: {ex.Message}";
        }
        finally
        {
            IsFetchingPullRequest = false;
        }
    }

    // --- Pull main ----------------------------------------------------------

    /// <summary>
    /// Enabled only when there is something to pull: an open repo with a remote and
    /// a detected main branch, and no other operation in flight.
    /// </summary>
    private bool CanPullMain() =>
        HasRepo && HasRemote && !string.IsNullOrEmpty(MainBranchName) && !IsBusy && !IsPulling;

    /// <summary>True while a pull is running, so the button can show progress.</summary>
    [ObservableProperty]
    private bool _isPulling;

    partial void OnIsPullingChanged(bool value) => PullMainCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Brings the main branch up to date from its remote. This is the only action in
    /// the app that writes to the repository; it is fast-forward only, and leaves the
    /// working tree alone unless main itself is checked out.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPullMain))]
    private async Task PullMain()
    {
        if (_workingDirectory is null)
            return;

        ErrorMessage = null;
        IsPulling = true;
        var previousStatus = Status;
        Status = $"Pulling {MainBranchName}…";
        try
        {
            var result = await _gitCommands.PullMainAsync(_workingDirectory, MainBranchName, CurrentBranch);

            if (!result.Success)
            {
                ErrorMessage = result.Message;
                Status = previousStatus;
                return;
            }

            // Refs moved underneath the open LibGit2Sharp handle, which caches them;
            // reopening gives the rest of the app a consistent view of the new state.
            var snapshot = await Task.Run(() => _git.OpenRepository(_workingDirectory));
            ApplySnapshot(snapshot); // also refills the commit picker, keeping the selection
            await RefreshComparisonAsync();
            Status = result.Message.Length > 0 ? result.Message : $"{MainBranchName} is up to date.";
        }
        catch (GitException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Pull failed: {ex.Message}";
        }
        finally
        {
            IsPulling = false;
        }
    }

    // --- Auto-refresh -------------------------------------------------------

    /// <summary>Whether the open repository is polled for new changes on an interval.</summary>
    [ObservableProperty]
    private bool _autoRefreshEnabled;

    /// <summary>Polling interval in seconds when <see cref="AutoRefreshEnabled"/> is on.</summary>
    public int AutoRefreshIntervalSeconds => Math.Max(1, _settings.Settings.AutoRefreshIntervalSeconds);

    /// <summary>Raised when auto-refresh settings change so the view can (re)arm its timer.</summary>
    public event Action? AutoRefreshConfigChanged;

    /// <summary>Signature of the last applied change set; lets auto-refresh skip no-op ticks.</summary>
    private string? _lastChangeSignature;
    private bool _autoRefreshing;

    partial void OnAutoRefreshEnabledChanged(bool value)
    {
        _settings.Settings.AutoRefreshEnabled = value;
        _settings.Save();
        AutoRefreshConfigChanged?.Invoke();
    }

    /// <summary>
    /// A quiet, best-effort poll: re-resolves the comparison and re-reads the change
    /// set on a worker thread, and only touches the UI when the set actually changed.
    /// Unlike <see cref="RefreshComparisonAsync"/> it shows no busy spinner and keeps
    /// the current file selection, so it never disrupts what the user is looking at.
    /// </summary>
    public async Task AutoRefreshAsync()
    {
        if (!AutoRefreshEnabled || !HasRepo || IsBusy || _autoRefreshing)
            return;

        _autoRefreshing = true;
        try
        {
            var mode = SelectedMode.Mode;
            var branch = SelectedBranch;
            var parent = ParentBranch;
            var commit = SelectedCommit?.Sha;

            var result = await Task.Run(() =>
            {
                var resolved = _git.ResolveComparison(mode, branch, parent, commit);
                if (!resolved.Found || resolved.Sha is null)
                    return (resolved, Changes: (IReadOnlyList<FileChange>?)null, Signature: SignatureFor(resolved.Sha, null));

                var changes = _git.GetChanges(resolved.Sha);
                return (resolved, Changes: (IReadOnlyList<FileChange>?)changes, Signature: SignatureFor(resolved.Sha, changes));
            });

            if (result.Signature == _lastChangeSignature)
                return; // nothing new since the last check

            ResolvedTargetLabel = result.resolved.Label;

            if (result.resolved.Found && result.resolved.Sha is { } sha && result.Changes is not null)
            {
                _currentBaseSha = sha;
                var keepPath = SelectedFile?.Path;
                PopulateFiles(result.Changes);     // refreshes _lastChangeSignature
                RestoreSelection(keepPath);
                Status = $"Comparing working tree against {result.resolved.Label}.";
            }
            else
            {
                _currentBaseSha = null;
                ClearFiles();
                _lastChangeSignature = result.Signature;
            }
        }
        catch
        {
            // Auto-refresh is best-effort; never surface its failures or disrupt the UI.
        }
        finally
        {
            _autoRefreshing = false;
        }
    }

    /// <summary>Re-selects the file at <paramref name="path"/> after a repopulate, if it survived.</summary>
    private void RestoreSelection(string? path)
    {
        if (path is null)
            return;

        var match = _allFiles.FirstOrDefault(f => f.Path == path);
        if (match is not null)
            SelectedFile = match;
    }

    /// <summary>Cheap fingerprint of a change set: base SHA plus each file's path and kind.</summary>
    private static string SignatureFor(string? sha, IReadOnlyList<FileChange>? changes)
    {
        if (changes is null)
            return $"{sha}|<none>";

        var sb = new System.Text.StringBuilder(sha);
        sb.Append('|');
        foreach (var c in changes.OrderBy(c => c.Path, StringComparer.Ordinal))
            sb.Append(c.Path).Append(':').Append((int)c.Kind).Append(';');
        return sb.ToString();
    }

    // --- Core flows ---------------------------------------------------------

    private async Task OpenPathAsync(string path)
    {
        ErrorMessage = null;
        using var _ = BeginBusy();
        try
        {
            var snapshot = await Task.Run(() => _git.OpenRepository(path));
            ApplySnapshot(snapshot);

            _settings.AddRecentRepository(snapshot.WorkingDirectory);
            ReloadRecentRepositories();

            HasRepo = true;
            IsRepoRibbonVisible = false; // collapse the picker once a repo is loaded
            await RefreshComparisonAsync();
        }
        catch (GitException ex)
        {
            HasRepo = false;
            ErrorMessage = ex.Message;
            Status = "Open a repository to begin.";
        }
        catch (Exception ex)
        {
            // Unexpected failures (native LibGit2Sharp / IO errors) must not crash
            // the app; fall back to the empty state with a readable message (NFR-4).
            HasRepo = false;
            ErrorMessage = $"Could not open the repository: {ex.Message}";
            Status = "Open a repository to begin.";
        }
    }

    private void ApplySnapshot(RepositorySnapshot snapshot)
    {
        RepositoryName = snapshot.Name;
        CurrentBranch = snapshot.CurrentBranch;
        HeadShortSha = snapshot.HeadShortSha;
        _workingDirectory = snapshot.WorkingDirectory;
        MainBranchName = snapshot.MainBranch;
        HasRemote = snapshot.HasRemote;
        _originUrl = snapshot.OriginUrl;
        PullMainCommand.NotifyCanExecuteChanged();
        OpenPullRequestDialogCommand.NotifyCanExecuteChanged();

        LocalBranches.Clear();
        foreach (var b in snapshot.LocalBranches)
            LocalBranches.Add(b.Name);

        // Pick sensible defaults without triggering a resolve per assignment;
        // a single RefreshComparisonAsync runs after HasRepo is set.
        var preferredBranch = snapshot.LocalBranches.FirstOrDefault(b => !b.IsCurrent)
                              ?? snapshot.LocalBranches.FirstOrDefault();
        SetField(() => SelectedBranch = preferredBranch?.Name);
        SetField(() => ParentBranch = snapshot.DefaultParentBranch);
        ReloadBranchCommits(snapshot.MainBranch, keepSha: SelectedCommit?.Sha);
    }

    /// <summary>
    /// Refills the commit picker for the open repository, preserving the current
    /// selection when that commit is still on the branch. Falls back to the branch
    /// point so <see cref="ComparisonMode.SinceCommit"/> always has something to
    /// resolve — including when the mode was restored from settings on launch.
    /// </summary>
    private void ReloadBranchCommits(string? mainBranch, string? keepSha)
    {
        IReadOnlyList<CommitInfo> commits;
        try
        {
            commits = _git.GetBranchCommits(mainBranch);
        }
        catch (Exception)
        {
            commits = Array.Empty<CommitInfo>();
        }

        BranchCommits.Clear();
        foreach (var c in commits)
            BranchCommits.Add(c);

        var restored = keepSha is null ? null : BranchCommits.FirstOrDefault(c => c.Sha == keepSha);
        var fallback = IsSinceCommitMode && BranchCommits.Count > 0 ? BranchCommits[^1] : null;
        SetField(() => SelectedCommit = restored ?? fallback);
    }

    private async Task RefreshComparisonAsync()
    {
        if (!HasRepo)
            return;

        using var _ = BeginBusy();
        try
        {
            var mode = SelectedMode.Mode;
            var branch = SelectedBranch;
            var parent = ParentBranch;
            var commit = SelectedCommit?.Sha;

            var resolved = await Task.Run(() => _git.ResolveComparison(mode, branch, parent, commit));

            ResolvedTargetLabel = resolved.Label;
            Status = resolved.Found
                ? $"Comparing working tree against {resolved.Label}."
                : resolved.Error ?? "Could not resolve the comparison target.";

            if (resolved.Found && resolved.Sha is { } sha)
            {
                _currentBaseSha = sha;
                var changes = await Task.Run(() => _git.GetChanges(sha));
                PopulateFiles(changes);
            }
            else
            {
                _currentBaseSha = null;
                ClearFiles();
            }

            _settings.Settings.LastComparisonMode = mode;
            _settings.Save();
        }
        catch (GitException ex)
        {
            Status = ex.Message;
            ClearFiles();
        }
        catch (Exception ex)
        {
            // Keep the open repo on screen but surface the failure (NFR-4).
            Status = $"Could not read changes: {ex.Message}";
            ClearFiles();
        }
    }

    private void PopulateFiles(IReadOnlyList<FileChange> changes)
    {
        _lastChangeSignature = SignatureFor(_currentBaseSha, changes);
        _allFiles = changes.Select(c => new FileChangeViewModel(c)).ToList();
        SelectedFile = null;
        ApplyFilter();
        StartStatsLoad(); // fill tracked-file +/- counts in the background
    }

    // --- Deferred line counts (NFR-1) ---------------------------------------

    /// <summary>Identifies the latest stats request so stale results are ignored.</summary>
    private int _statsSequence;

    private async void StartStatsLoad()
    {
        var seq = ++_statsSequence;
        var sha = _currentBaseSha;
        if (sha is null)
            return;

        var byPath = new Dictionary<string, FileChangeViewModel>(StringComparer.Ordinal);
        foreach (var f in _allFiles)
            byPath[f.Path] = f;

        IReadOnlyDictionary<string, FileStats> stats;
        try
        {
            stats = await Task.Run(() => _git.GetChangeStats(sha));
        }
        catch
        {
            return; // counts are a nicety; never let a background failure surface
        }

        if (seq != _statsSequence)
            return; // superseded by a newer refresh

        foreach (var (path, stat) in stats)
            if (byPath.TryGetValue(path, out var vm))
                vm.ApplyStats(stat);

        UpdateChangedFilesSummary();
    }

    partial void OnSelectedFileChanged(FileChangeViewModel? value)
    {
        if (value is null || _currentBaseSha is null)
        {
            Diff.Clear();
        }
        else
        {
            _ = Diff.LoadAsync(_currentBaseSha, value.Model, _nextLoadPosition);
            _nextLoadPosition = HunkPosition.First;
        }

        SyncSelectedNode(value);
    }

    /// <summary>Selecting a file node shows its diff; folder nodes are inert.</summary>
    partial void OnSelectedNodeChanged(FileTreeNode? value)
    {
        if (value is FileNode fn)
            SelectedFile = fn.File;
    }

    /// <summary>Highlights the tree node for <paramref name="file"/>, expanding its
    /// ancestors so it is visible. Setting the same node again is a no-op, so this
    /// stays loop-free with <see cref="OnSelectedNodeChanged"/>.</summary>
    private void SyncSelectedNode(FileChangeViewModel? file)
    {
        if (file is null)
        {
            SelectedNode = null;
            return;
        }

        var node = _orderedFileNodes.FirstOrDefault(n => n.File == file);
        if (node is null)
            return;

        for (var p = node.Parent; p is not null; p = p.Parent)
            p.IsExpanded = true;

        SelectedNode = node;
    }

    // --- Keyboard navigation (FR-24..27) ------------------------------------

    private HunkPosition _nextLoadPosition = HunkPosition.First;

    public IReadOnlyList<ShortcutInfo> Shortcuts => KeyboardShortcuts.All;
    public string ShortcutHint => KeyboardShortcuts.StatusHint;

    [ObservableProperty]
    private bool _isHelpVisible;

    /// <summary>Event raised to ask the view to focus the file filter (FR-25).</summary>
    public event Action? FocusFilterRequested;

    /// <summary>
    /// Selects the next file, stopping at the last one rather than wrapping.
    /// Returns false when the selection did not move, so callers can tell a real
    /// step from a clamped no-op.
    /// </summary>
    public bool NextFile()
    {
        if (_orderedFileNodes.Count == 0)
            return false;

        var index = CurrentFileNodeIndex();
        var target = Math.Min(index + 1, _orderedFileNodes.Count - 1);
        if (target == index)
            return false;

        SelectedFile = _orderedFileNodes[target].File;
        return true;
    }

    /// <summary>
    /// Selects the previous file, stopping at the first one rather than wrapping.
    /// Returns false when the selection did not move.
    /// </summary>
    public bool PreviousFile()
    {
        if (_orderedFileNodes.Count == 0)
            return false;

        var index = SelectedFile is null ? _orderedFileNodes.Count : CurrentFileNodeIndex();
        var target = Math.Max(index - 1, 0);
        if (target == index)
            return false;

        SelectedFile = _orderedFileNodes[target].File;
        return true;
    }

    private int CurrentFileNodeIndex() =>
        SelectedFile is null ? -1 : _orderedFileNodes.FindIndex(n => n.File == SelectedFile);

    /// <summary>
    /// Steps to the next changed section within the open file, crossing into the next
    /// file's first section only once this one is exhausted (FR-24, FR-27).
    /// </summary>
    public void NextChange()
    {
        // A file whose diff is still streaming has an incomplete section list, so
        // "no next section" means "not yet", not "done with this file". Falling
        // through here would skip the file's changes on a quick second keypress.
        if (Diff.IsLoading)
            return;

        if (!Diff.GoToNextSection())
            NextFile(); // falls through to the next file's first section (FR-27)
    }

    /// <summary>
    /// Steps to the previous changed section within the open file, crossing into the
    /// previous file's last section only once this one is exhausted (FR-24, FR-27).
    /// </summary>
    public void PreviousChange()
    {
        if (Diff.IsLoading)
            return; // see NextChange: an incomplete section list is not an exhausted one

        if (Diff.GoToPreviousSection())
            return;

        // Land on the previous file's last section (FR-27). OnSelectedFileChanged
        // consumes the flag and resets it -- but only if the selection actually moves,
        // so clear it here when there is no previous file, otherwise the next file the
        // user opens would be scrolled to its last section instead of its first.
        _nextLoadPosition = HunkPosition.Last;
        if (!PreviousFile())
            _nextLoadPosition = HunkPosition.First;
    }

    public void ToggleDiffLayout() => Diff.ToggleLayoutCommand.Execute(null);

    public void ToggleTheme()
    {
        var index = 0;
        for (var i = 0; i < ThemeModes.Count; i++)
        {
            if (ThemeModes[i] == SelectedTheme)
            {
                index = i;
                break;
            }
        }
        SelectedTheme = ThemeModes[(index + 1) % ThemeModes.Count];
    }

    public void RequestFocusFilter() => FocusFilterRequested?.Invoke();

    public void ToggleHelp() => IsHelpVisible = !IsHelpVisible;

    [RelayCommand]
    private void CloseHelp() => IsHelpVisible = false;

    private void ClearFiles()
    {
        _statsSequence++; // invalidate any in-flight background stats
        _lastChangeSignature = null;
        _allFiles = new List<FileChangeViewModel>();
        RootNodes.Clear();
        _orderedFileNodes.Clear();
        SelectedNode = null;
        SelectedFile = null;
        ChangedFilesSummary = string.Empty;
    }

    private void ApplyFilter()
    {
        var filter = FileFilter?.Trim();

        var visible = string.IsNullOrEmpty(filter)
            ? _allFiles
            : _allFiles.Where(f => f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        BuildTree(visible);
        UpdateChangedFilesSummary(visible.Count);

        // Re-point the tree's highlight at the still-selected file, if it survived
        // the filter. Leaves the diff untouched (SelectedFile is unchanged).
        SyncSelectedNode(SelectedFile);
    }

    /// <summary>Recomputes the "N changed files  +A −D" summary. Counts reflect
    /// whatever line stats are currently known (they fill in asynchronously).</summary>
    private void UpdateChangedFilesSummary(int? shownCount = null)
    {
        var filter = FileFilter?.Trim();
        var total = _allFiles.Count;
        var shown = shownCount ?? (string.IsNullOrEmpty(filter)
            ? total
            : _allFiles.Count(f => f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        var added = _allFiles.Sum(f => f.LinesAdded);
        var deleted = _allFiles.Sum(f => f.LinesDeleted);

        var countText = string.IsNullOrEmpty(filter) || shown == total
            ? $"{total} changed file{(total == 1 ? "" : "s")}"
            : $"{shown} of {total} files";

        ChangedFilesSummary = total == 0 ? "No changes" : $"{countText}    +{added}  −{deleted}";
    }

    // --- Folder/file tree construction --------------------------------------

    private void BuildTree(IReadOnlyList<FileChangeViewModel> files)
    {
        RootNodes.Clear();
        _orderedFileNodes.Clear();

        var folders = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var rootFolders = new List<FolderNode>();
        var rootFiles = new List<FileNode>();

        foreach (var f in files)
        {
            var dir = f.Directory; // forward-slashed directory, or null at the repo root
            if (string.IsNullOrEmpty(dir))
            {
                rootFiles.Add(new FileNode(f, null));
                continue;
            }

            var parent = EnsureFolder(dir, folders, rootFolders);
            parent.Children.Add(new FileNode(f, parent));
            for (var p = parent; p is not null; p = p.Parent)
                p.ChangedCount++;
        }

        // Order every level: subfolders (alpha) before files (alpha).
        foreach (var folder in rootFolders)
            SortFolder(folder);

        foreach (var folder in rootFolders.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            RootNodes.Add(folder);
        foreach (var file in rootFiles.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            RootNodes.Add(file);

        foreach (var node in RootNodes)
            CollectFileNodes(node);
    }

    /// <summary>Finds or creates the folder chain for a forward-slashed directory path.</summary>
    private static FolderNode EnsureFolder(
        string dir, Dictionary<string, FolderNode> folders, List<FolderNode> rootFolders)
    {
        if (folders.TryGetValue(dir, out var existing))
            return existing;

        var slash = dir.LastIndexOf('/');
        FolderNode node;
        if (slash < 0)
        {
            node = new FolderNode(dir, null);
            rootFolders.Add(node);
        }
        else
        {
            var parent = EnsureFolder(dir[..slash], folders, rootFolders);
            node = new FolderNode(dir[(slash + 1)..], parent);
            parent.Children.Add(node);
        }

        folders[dir] = node;
        return node;
    }

    private static void SortFolder(FolderNode folder)
    {
        var subfolders = folder.Children.OfType<FolderNode>()
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var files = folder.Children.OfType<FileNode>()
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();

        folder.Children.Clear();
        foreach (var sub in subfolders)
        {
            folder.Children.Add(sub);
            SortFolder(sub);
        }
        foreach (var file in files)
            folder.Children.Add(file);
    }

    private void CollectFileNodes(FileTreeNode node)
    {
        switch (node)
        {
            case FileNode fn:
                _orderedFileNodes.Add(fn);
                break;
            case FolderNode folder:
                foreach (var child in folder.Children)
                    CollectFileNodes(child);
                break;
        }
    }

    // --- Recent repositories ------------------------------------------------

    private void ReloadRecentRepositories()
    {
        RecentRepositories.Clear();
        foreach (var r in _settings.Settings.RecentRepositories)
            RecentRepositories.Add(new RecentRepositoryViewModel(r, OpenPathAsyncFromRecent, RemoveRecent));

        HasRecentRepositories = RecentRepositories.Count > 0;
    }

    private Task OpenPathAsyncFromRecent(RecentRepositoryViewModel vm) => OpenPathAsync(vm.Path);

    private void RemoveRecent(RecentRepositoryViewModel vm)
    {
        _settings.RemoveRecentRepository(vm.Path);
        ReloadRecentRepositories();
    }

    // --- Helpers ------------------------------------------------------------

    /// <summary>
    /// Sets <see cref="IsBusy"/> for the lifetime of the returned scope. Nesting is
    /// reference-counted so an outer open that awaits an inner refresh stays busy
    /// until the outermost scope is disposed.
    /// </summary>
    private IDisposable BeginBusy()
    {
        _busyDepth++;
        IsBusy = true;
        return new BusyScope(this);
    }

    private int _busyDepth;

    private void EndBusy()
    {
        if (--_busyDepth <= 0)
        {
            _busyDepth = 0;
            IsBusy = false;
        }
    }

    private sealed class BusyScope(MainWindowViewModel owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.EndBusy();
        }
    }

    /// <summary>Re-entrancy guard so default assignments during open don't each resolve.</summary>
    private bool _suppressRefresh;

    private void TriggerRefresh()
    {
        if (_suppressRefresh || !HasRepo)
            return;
        _ = RefreshComparisonAsync();
    }

    private void SetField(System.Action assign)
    {
        _suppressRefresh = true;
        try { assign(); }
        finally { _suppressRefresh = false; }
    }
}
