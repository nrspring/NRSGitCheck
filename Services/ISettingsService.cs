using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Loads and persists <see cref="AppSettings"/> and maintains the recent-repository
/// history. The current settings are kept in memory and saved on mutation.
/// </summary>
public interface ISettingsService
{
    /// <summary>The current in-memory settings (always non-null).</summary>
    AppSettings Settings { get; }

    /// <summary>Reloads settings from disk, falling back to defaults if missing/corrupt.</summary>
    void Load();

    /// <summary>Persists the current settings to disk (best-effort).</summary>
    void Save();

    /// <summary>
    /// Records a repository as most-recently opened: de-duplicates, moves it to the
    /// front, caps the list length, and saves (FR-3..4).
    /// </summary>
    void AddRecentRepository(string repositoryPath);

    /// <summary>Removes a repository from the history and saves (FR-5).</summary>
    void RemoveRecentRepository(string repositoryPath);
}
