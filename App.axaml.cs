using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using NRSGitCheck.Services;
using NRSGitCheck.ViewModels;
using NRSGitCheck.Views;

namespace NRSGitCheck;

public partial class App : Application
{
    /// <summary>Application-wide service container (composition root).</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Registers views, view models, and services. Services land here as the
    /// later phases introduce them (Git, settings, theming, diff, highlighting).
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Services
        services.AddSingleton<ISettingsService>(_ => new SettingsService(SettingsService.DefaultFilePath));
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IDiffService, DiffService>();
        services.AddSingleton<IFolderPickerService, FolderPickerService>();

        // Views
        services.AddTransient<MainWindow>();

        // View models
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<DiffViewModel>();
    }
}
