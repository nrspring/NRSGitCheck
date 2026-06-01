using System;
using System.IO;
using System.Text.Json.Serialization;

namespace NRSGitCheck.Models;

/// <summary>
/// One entry in the recent-repository history (FR-3..5).
/// </summary>
public sealed class RecentRepository
{
    /// <summary>Absolute, normalized path to the repository working directory.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Display name (the working-directory folder name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the repository was last opened (used for most-recent-first ordering).</summary>
    public DateTimeOffset LastOpenedUtc { get; set; }

    /// <summary>
    /// Whether the folder still exists on disk. Used by the UI to flag stale
    /// history entries (FR-5). Not persisted.
    /// </summary>
    [JsonIgnore]
    public bool DirectoryExists =>
        !string.IsNullOrWhiteSpace(Path) && Directory.Exists(Path);
}
