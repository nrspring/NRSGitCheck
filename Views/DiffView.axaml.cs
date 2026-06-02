using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NRSGitCheck.ViewModels;

namespace NRSGitCheck.Views;

public partial class DiffView : UserControl
{
    private DiffViewModel? _vm;

    private ScrollViewer? _leftScroll;
    private ScrollViewer? _rightScroll;
    private bool _syncing;

    public DiffView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Keep the two side-by-side panes vertically aligned. ScrollChanged is a
        // routed event, so we can listen on the ListBox even before its inner
        // ScrollViewer is realized (the side view starts collapsed).
        this.FindControl<ListBox>("SideLeftList")
            ?.AddHandler(ScrollViewer.ScrollChangedEvent, OnLeftScrolled);
        this.FindControl<ListBox>("SideRightList")
            ?.AddHandler(ScrollViewer.ScrollChangedEvent, OnRightScrolled);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null)
            _vm.ScrollToRequested -= OnScrollToRequested;

        _vm = DataContext as DiffViewModel;

        if (_vm is not null)
            _vm.ScrollToRequested += OnScrollToRequested;
    }

    private void OnLeftScrolled(object? sender, ScrollChangedEventArgs e)
    {
        _leftScroll ??= FindScrollViewer("SideLeftList");
        _rightScroll ??= FindScrollViewer("SideRightList");
        SyncOffset(_leftScroll, _rightScroll);
    }

    private void OnRightScrolled(object? sender, ScrollChangedEventArgs e)
    {
        _leftScroll ??= FindScrollViewer("SideLeftList");
        _rightScroll ??= FindScrollViewer("SideRightList");
        SyncOffset(_rightScroll, _leftScroll);
    }

    // Mirror the source pane's scroll offset (both axes) onto the target. The
    // guard prevents the resulting ScrollChanged from bouncing back into an
    // infinite loop.
    private void SyncOffset(ScrollViewer? from, ScrollViewer? to)
    {
        if (_syncing || from is null || to is null)
            return;

        if (to.Offset == from.Offset)
            return;

        _syncing = true;
        to.Offset = from.Offset;
        _syncing = false;
    }

    private ScrollViewer? FindScrollViewer(string name) =>
        this.FindControl<ListBox>(name)?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void OnScrollToRequested(object row)
    {
        // Defer so the ListBox has realized the (possibly just-changed) items.
        Dispatcher.UIThread.Post(() =>
        {
            var list = _vm?.IsInline == true ? this.FindControl<ListBox>("InlineList")
                                             : this.FindControl<ListBox>("SideLeftList");
            list?.ScrollIntoView(row);
        }, DispatcherPriority.Background);
    }
}
