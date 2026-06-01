using System;
using Avalonia.Controls;
using NRSGitCheck.ViewModels;

namespace NRSGitCheck.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Kick off recent-repo load and optional reopen once the window exists
        // (the folder picker and storage provider need a live TopLevel).
        if (DataContext is MainWindowViewModel vm)
            _ = vm.InitializeAsync();
    }
}
