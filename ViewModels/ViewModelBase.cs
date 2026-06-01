using CommunityToolkit.Mvvm.ComponentModel;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// Base class for all view models. Derives from CommunityToolkit's
/// <see cref="ObservableObject"/> to provide INotifyPropertyChanged plumbing
/// and the [ObservableProperty] / [RelayCommand] source generators.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}
