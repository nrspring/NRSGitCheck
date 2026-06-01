using Avalonia.Controls;
using Avalonia.Threading;
using NRSGitCheck.ViewModels;

namespace NRSGitCheck.Views;

public partial class DiffView : UserControl
{
    private DiffViewModel? _vm;

    public DiffView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null)
            _vm.ScrollToRequested -= OnScrollToRequested;

        _vm = DataContext as DiffViewModel;

        if (_vm is not null)
            _vm.ScrollToRequested += OnScrollToRequested;
    }

    private void OnScrollToRequested(object row)
    {
        // Defer so the ListBox has realized the (possibly just-changed) items.
        Dispatcher.UIThread.Post(() =>
        {
            var list = _vm?.IsInline == true ? this.FindControl<ListBox>("InlineList")
                                             : this.FindControl<ListBox>("SideList");
            list?.ScrollIntoView(row);
        }, DispatcherPriority.Background);
    }
}
