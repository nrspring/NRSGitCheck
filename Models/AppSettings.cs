using System.Collections.Generic;

namespace NRSGitCheck.Models;

/// <summary>
/// Root of the persisted application settings (FR-33). Serialized to
/// <c>%APPDATA%\NRSGitCheck\settings.json</c>.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Light/dark appearance mode. Defaults to following the OS (FR-29).</summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    /// <summary>Whether to reopen the most recent repository on launch (FR-6).</summary>
    public bool ReopenLastRepoOnLaunch { get; set; } = true;

    /// <summary>The comparison mode last used, restored on next launch.</summary>
    public ComparisonMode LastComparisonMode { get; set; } = ComparisonMode.LastCommit;

    /// <summary>The diff layout last used, restored on next launch.</summary>
    public DiffLayout LastDiffLayout { get; set; } = DiffLayout.SideBySide;

    /// <summary>Recently opened repositories, most-recent first (FR-3, FR-4).</summary>
    public List<RecentRepository> RecentRepositories { get; set; } = new();
}
