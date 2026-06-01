using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// One row in the recent-repository history (FR-3..5). Open/remove are delegated
/// back to the owning <see cref="MainWindowViewModel"/> so binding stays simple.
/// </summary>
public sealed partial class RecentRepositoryViewModel : ViewModelBase
{
    private readonly Func<RecentRepositoryViewModel, Task> _open;
    private readonly Action<RecentRepositoryViewModel> _remove;

    public RecentRepositoryViewModel(
        RecentRepository model,
        Func<RecentRepositoryViewModel, Task> open,
        Action<RecentRepositoryViewModel> remove)
    {
        Name = model.Name;
        Path = model.Path;
        DirectoryExists = model.DirectoryExists;
        _open = open;
        _remove = remove;
    }

    public string Name { get; }
    public string Path { get; }

    /// <summary>False for history entries whose folder no longer exists (FR-5).</summary>
    public bool DirectoryExists { get; }

    [RelayCommand]
    private Task Open() => _open(this);

    [RelayCommand]
    private void Remove() => _remove(this);
}
