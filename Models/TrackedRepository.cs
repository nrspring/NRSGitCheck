using System.IO;
using System.Text.Json.Serialization;

namespace NRSGitCheck.Models;

/// <summary>
/// A repository pinned to the Repositories tab. Unlike <see cref="RecentRepository"/>
/// this list is curated by hand: entries are added and removed explicitly, and are
/// never evicted to make room for newer ones.
/// </summary>
public sealed class TrackedRepository
{
    /// <summary>Absolute, normalized path to the repository working directory.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Display name (the working-directory folder name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the folder still exists on disk. Not persisted.</summary>
    [JsonIgnore]
    public bool DirectoryExists =>
        !string.IsNullOrWhiteSpace(Path) && Directory.Exists(Path);
}
