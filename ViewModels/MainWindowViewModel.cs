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
    private readonly IFolderPickerService _folderPicker;

    private readonly IThemeService _themeService;

    public MainWindowViewModel(
        ISettingsService settings,
        IGitService git,
        IFolderPickerService folderPicker,
        DiffViewModel diff,
        IThemeService themeService)
    {
        _settings = settings;
        _git = git;
        _folderPicker = folderPicker;
        _themeService = themeService;
        Diff = diff;

        _selectedMode = ComparisonModes.FirstOrDefault(o => o.Mode == settings.Settings.LastComparisonMode)
                        ?? ComparisonModes[0];
        _selectedTheme = ThemeModes.FirstOrDefault(o => o.Mode == settings.Settings.ThemeMode)
                        ?? ThemeModes[0];

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

    /// <summary>The resolved base commit SHA for the current comparison, if any.</summary>
    private string? _currentBaseSha;

    // --- Shell chrome -------------------------------------------------------

    [ObservableProperty]
    private string _title = "NRSGitCheck";

    [ObservableProperty]
    private string _status = "Open a repository to begin.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    // --- Repository state ---------------------------------------------------

    [ObservableProperty]
    private bool _hasRepo;

    [ObservableProperty]
    private string? _repositoryName;

    [ObservableProperty]
    private string? _currentBranch;

    [ObservableProperty]
    private string? _headShortSha;

    [ObservableProperty]
    private string _resolvedTargetLabel = string.Empty;

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
        new ComparisonModeOption(ComparisonMode.LastCommit, "Last commit (HEAD)"),
        new ComparisonModeOption(ComparisonMode.OtherBranch, "Another branch"),
        new ComparisonModeOption(ComparisonMode.BranchBase, "Branch base (merge-base)"),
    };

    [ObservableProperty]
    private ComparisonModeOption _selectedMode;

    [ObservableProperty]
    private string? _selectedBranch;

    [ObservableProperty]
    private string? _parentBranch;

    public bool IsOtherBranchMode => SelectedMode?.Mode == ComparisonMode.OtherBranch;
    public bool IsBranchBaseMode => SelectedMode?.Mode == ComparisonMode.BranchBase;

    partial void OnSelectedModeChanged(ComparisonModeOption value)
    {
        OnPropertyChanged(nameof(IsOtherBranchMode));
        OnPropertyChanged(nameof(IsBranchBaseMode));
        TriggerRefresh();
    }

    partial void OnSelectedBranchChanged(string? value) => TriggerRefresh();
    partial void OnParentBranchChanged(string? value) => TriggerRefresh();

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

        LocalBranches.Clear();
        foreach (var b in snapshot.LocalBranches)
            LocalBranches.Add(b.Name);

        // Pick sensible defaults without triggering a resolve per assignment;
        // a single RefreshComparisonAsync runs after HasRepo is set.
        var preferredBranch = snapshot.LocalBranches.FirstOrDefault(b => !b.IsCurrent)
                              ?? snapshot.LocalBranches.FirstOrDefault();
        SetField(() => SelectedBranch = preferredBranch?.Name);
        SetField(() => ParentBranch = snapshot.DefaultParentBranch);
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

            var resolved = await Task.Run(() => _git.ResolveComparison(mode, branch, parent));

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
        _allFiles = changes.Select(c => new FileChangeViewModel(c)).ToList();
        SelectedFile = null;
        ApplyFilter();
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

    public void NextFile()
    {
        if (_orderedFileNodes.Count == 0)
            return;
        var index = CurrentFileNodeIndex();
        SelectedFile = _orderedFileNodes[Math.Min(index + 1, _orderedFileNodes.Count - 1)].File;
    }

    public void PreviousFile()
    {
        if (_orderedFileNodes.Count == 0)
            return;
        var index = SelectedFile is null ? _orderedFileNodes.Count : CurrentFileNodeIndex();
        SelectedFile = _orderedFileNodes[Math.Max(index - 1, 0)].File;
    }

    private int CurrentFileNodeIndex() =>
        SelectedFile is null ? -1 : _orderedFileNodes.FindIndex(n => n.File == SelectedFile);

    public void NextHunk()
    {
        if (!Diff.GoToNextHunk())
            NextFile(); // falls through to the next file's first hunk (FR-27)
    }

    public void PreviousHunk()
    {
        if (!Diff.GoToPreviousHunk())
        {
            _nextLoadPosition = HunkPosition.Last;
            PreviousFile(); // lands on the previous file's last hunk (FR-27)
        }
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

        var total = _allFiles.Count;
        var shown = visible.Count;
        var added = _allFiles.Sum(f => f.LinesAdded);
        var deleted = _allFiles.Sum(f => f.LinesDeleted);

        var countText = string.IsNullOrEmpty(filter) || shown == total
            ? $"{total} changed file{(total == 1 ? "" : "s")}"
            : $"{shown} of {total} files";

        ChangedFilesSummary = total == 0 ? "No changes" : $"{countText}    +{added}  −{deleted}";

        // Re-point the tree's highlight at the still-selected file, if it survived
        // the filter. Leaves the diff untouched (SelectedFile is unchanged).
        SyncSelectedNode(SelectedFile);
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
