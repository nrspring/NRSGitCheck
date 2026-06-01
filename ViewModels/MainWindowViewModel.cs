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

    public ObservableCollection<FileChangeViewModel> Files { get; } = new();

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
        IsBusy = true;
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
        finally
        {
            IsBusy = false;
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
            Diff.Clear();
        else
            _ = Diff.LoadAsync(_currentBaseSha, value.Model);
    }

    private void ClearFiles()
    {
        _allFiles = new List<FileChangeViewModel>();
        Files.Clear();
        SelectedFile = null;
        ChangedFilesSummary = string.Empty;
    }

    private void ApplyFilter()
    {
        var filter = FileFilter?.Trim();

        Files.Clear();
        foreach (var f in _allFiles)
        {
            if (string.IsNullOrEmpty(filter) ||
                f.Path.Contains(filter, System.StringComparison.OrdinalIgnoreCase))
                Files.Add(f);
        }

        var total = _allFiles.Count;
        var shown = Files.Count;
        var added = _allFiles.Sum(f => f.LinesAdded);
        var deleted = _allFiles.Sum(f => f.LinesDeleted);

        var countText = string.IsNullOrEmpty(filter) || shown == total
            ? $"{total} changed file{(total == 1 ? "" : "s")}"
            : $"{shown} of {total} files";

        ChangedFilesSummary = total == 0 ? "No changes" : $"{countText}    +{added}  −{deleted}";
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
