using NRSGitCheck.Models;
using NRSGitCheck.Services;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Phase 7 (persistence half): the selected mode is stored and survives a restart.
/// The live OS-follow behavior requires a running Avalonia app and is verified
/// manually. <see cref="ThemeService"/> no-ops its UI calls when there is no app.
/// </summary>
public sealed class ThemeServiceTests
{
    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public int SaveCount { get; private set; }
        public void Load() { }
        public void Save() => SaveCount++;
        public void AddRecentRepository(string repositoryPath) { }
        public void RemoveRecentRepository(string repositoryPath) { }
    }

    [Fact]
    public void Initial_mode_comes_from_settings()
    {
        var settings = new StubSettings();
        settings.Settings.ThemeMode = ThemeMode.Light;

        var svc = new ThemeService(settings, new NullSyntaxHighlighter());

        Assert.Equal(ThemeMode.Light, svc.Mode);
    }

    [Fact]
    public void SetMode_persists_and_survives_restart()
    {
        var settings = new StubSettings();
        var svc = new ThemeService(settings, new NullSyntaxHighlighter());

        svc.SetMode(ThemeMode.Dark);

        Assert.Equal(ThemeMode.Dark, svc.Mode);
        Assert.Equal(ThemeMode.Dark, settings.Settings.ThemeMode);
        Assert.True(settings.SaveCount > 0);

        // A new service over the same settings ("restart") restores the override.
        var restarted = new ThemeService(settings, new NullSyntaxHighlighter());
        Assert.Equal(ThemeMode.Dark, restarted.Mode);
    }
}
