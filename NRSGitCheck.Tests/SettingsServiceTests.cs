using System;
using System.IO;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Verifies the Phase 1 exit check: settings round-trip across "restarts"
/// (new service instances over the same file) and recent-repo list behavior.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NRSGitCheckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var svc = new SettingsService(_file);

        Assert.Equal(ThemeMode.System, svc.Settings.ThemeMode);
        Assert.True(svc.Settings.ReopenLastRepoOnLaunch);
        Assert.Equal(ComparisonMode.LastCommit, svc.Settings.LastComparisonMode);
        Assert.Equal(DiffLayout.SideBySide, svc.Settings.LastDiffLayout);
        Assert.Empty(svc.Settings.RecentRepositories);
    }

    [Fact]
    public void Settings_round_trip_across_restart()
    {
        var first = new SettingsService(_file);
        first.Settings.ThemeMode = ThemeMode.Dark;
        first.Settings.LastDiffLayout = DiffLayout.Inline;
        first.Settings.ReopenLastRepoOnLaunch = false;
        first.Save();

        // A new instance simulates a fresh application launch.
        var second = new SettingsService(_file);

        Assert.Equal(ThemeMode.Dark, second.Settings.ThemeMode);
        Assert.Equal(DiffLayout.Inline, second.Settings.LastDiffLayout);
        Assert.False(second.Settings.ReopenLastRepoOnLaunch);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        File.WriteAllText(_file, "{ this is not valid json ]");

        var svc = new SettingsService(_file);

        Assert.Equal(ThemeMode.System, svc.Settings.ThemeMode);
        Assert.Empty(svc.Settings.RecentRepositories);
    }

    [Fact]
    public void Recent_repos_persist_and_are_most_recent_first()
    {
        var a = Path.Combine(_dir, "repoA");
        var b = Path.Combine(_dir, "repoB");

        var svc = new SettingsService(_file);
        svc.AddRecentRepository(a);
        svc.AddRecentRepository(b);

        var reloaded = new SettingsService(_file);
        Assert.Equal(2, reloaded.Settings.RecentRepositories.Count);
        Assert.EndsWith("repoB", reloaded.Settings.RecentRepositories[0].Path);
        Assert.EndsWith("repoA", reloaded.Settings.RecentRepositories[1].Path);
        Assert.Equal("repoB", reloaded.Settings.RecentRepositories[0].Name);
    }

    [Fact]
    public void Re_adding_repo_dedupes_and_moves_to_front()
    {
        var a = Path.Combine(_dir, "repoA");
        var b = Path.Combine(_dir, "repoB");

        var svc = new SettingsService(_file);
        svc.AddRecentRepository(a);
        svc.AddRecentRepository(b);
        svc.AddRecentRepository(a); // re-open A

        Assert.Equal(2, svc.Settings.RecentRepositories.Count);
        Assert.EndsWith("repoA", svc.Settings.RecentRepositories[0].Path);
    }

    [Fact]
    public void Remove_recent_repo_persists()
    {
        var a = Path.Combine(_dir, "repoA");

        var svc = new SettingsService(_file);
        svc.AddRecentRepository(a);
        svc.RemoveRecentRepository(a);

        Assert.Empty(new SettingsService(_file).Settings.RecentRepositories);
    }

    [Fact]
    public void Recent_repo_flags_missing_directory()
    {
        var existing = Path.Combine(_dir, "exists");
        Directory.CreateDirectory(existing);
        var missing = Path.Combine(_dir, "gone");

        var svc = new SettingsService(_file);
        svc.AddRecentRepository(existing);
        svc.AddRecentRepository(missing);

        var byName = svc.Settings.RecentRepositories;
        Assert.True(byName.Find(r => r.Name == "exists")!.DirectoryExists);
        Assert.False(byName.Find(r => r.Name == "gone")!.DirectoryExists);
    }
}
