using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using NRSGitCheck.ViewModels;

namespace NRSGitCheck.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;
    private DispatcherTimer? _autoRefreshTimer;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Kick off recent-repo load and optional reopen once the window exists
        // (the folder picker and storage provider need a live TopLevel).
        if (DataContext is MainWindowViewModel vm)
            _ = vm.InitializeAsync();

        ConfigureAutoRefresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoRefreshTimer?.Stop();
        if (_vm is not null)
            _vm.AutoRefreshConfigChanged -= ConfigureAutoRefresh;
        base.OnClosed(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.FocusFilterRequested -= FocusFilter;
            _vm.AutoRefreshConfigChanged -= ConfigureAutoRefresh;
        }

        _vm = DataContext as MainWindowViewModel;

        if (_vm is not null)
        {
            _vm.FocusFilterRequested += FocusFilter;
            _vm.AutoRefreshConfigChanged += ConfigureAutoRefresh;
        }

        ConfigureAutoRefresh();
    }

    /// <summary>(Re)arms the background poll that asks the view model to check the
    /// repository for new changes, honoring the enabled flag and interval.</summary>
    private void ConfigureAutoRefresh()
    {
        _autoRefreshTimer?.Stop();

        if (_vm is null || !_vm.AutoRefreshEnabled)
            return;

        _autoRefreshTimer ??= new DispatcherTimer();
        _autoRefreshTimer.Interval = TimeSpan.FromSeconds(_vm.AutoRefreshIntervalSeconds);
        _autoRefreshTimer.Tick -= OnAutoRefreshTick;
        _autoRefreshTimer.Tick += OnAutoRefreshTick;
        _autoRefreshTimer.Start();
    }

    private void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _ = _vm.AutoRefreshAsync();
    }

    private void FocusFilter() => this.FindControl<TextBox>("FilterBox")?.Focus();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainWindowViewModel vm)
            return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.Escape && vm.IsHelpVisible) { vm.CloseHelpCommand.Execute(null); e.Handled = true; return; }

        // Modifier / function shortcuts work regardless of focus.
        if (ctrl && e.Key == Key.O) { vm.OpenRepositoryCommand.Execute(null); e.Handled = true; return; }
        if (e.Key == Key.F5) { vm.RefreshCommand.Execute(null); e.Handled = true; return; }
        if (ctrl && e.Key == Key.L) { vm.ToggleDiffLayout(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.T) { vm.ToggleTheme(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { vm.RequestFocusFilter(); e.Handled = true; return; }
        if (e.Key == Key.F1) { vm.ToggleHelp(); e.Handled = true; return; }
        if (alt && e.Key == Key.Down) { vm.NextHunk(); e.Handled = true; return; }
        if (alt && e.Key == Key.Up) { vm.PreviousHunk(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Down) { vm.NextFile(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Up) { vm.PreviousFile(); e.Handled = true; return; }

        // Bare-key shortcuts must not fire while typing in a text field.
        if (IsTextInputFocused())
            return;

        switch (e.Key)
        {
            case Key.J: vm.NextHunk(); e.Handled = true; break;
            case Key.K: vm.PreviousHunk(); e.Handled = true; break;
            case Key.OemCloseBrackets: vm.NextFile(); e.Handled = true; break;   // ]
            case Key.OemOpenBrackets: vm.PreviousFile(); e.Handled = true; break; // [
            case Key.OemQuestion when shift: vm.ToggleHelp(); e.Handled = true; break; // ?
        }
    }

    private bool IsTextInputFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox or AutoCompleteBox;
    }
}
