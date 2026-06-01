using CommunityToolkit.Mvvm.ComponentModel;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// View model for the single application window. In Phase 0 this only carries
/// the shell's title and status text; the comparison/file-list/diff state is
/// added in later phases.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "NRSGitCheck";

    [ObservableProperty]
    private string _status = "Ready";
}
