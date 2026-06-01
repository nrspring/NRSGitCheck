using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// JSON-file-backed implementation of <see cref="ISettingsService"/>. The settings
/// file path is injectable so the service can be unit-tested without touching the
/// real AppData location.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private const int MaxRecentRepositories = 15;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    /// <summary>The default settings location: <c>%APPDATA%\NRSGitCheck\settings.json</c> (FR-32).</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NRSGitCheck",
        "settings.json");

    public void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            else
            {
                Settings = new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings → fall back to defaults without crashing (FR-34).
            Settings = new AppSettings();
        }

        // A deserialized payload may legally omit the list.
        Settings.RecentRepositories ??= new();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(Settings, JsonOptions);

            // Write to a temp file then swap in, so a crash mid-write can't truncate
            // the existing settings file.
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(_filePath))
                File.Replace(tmp, _filePath, null);
            else
                File.Move(tmp, _filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort; never crash the app over a failed write.
        }
    }

    public void AddRecentRepository(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            return;

        var normalized = NormalizePath(repositoryPath);

        // De-duplicate (case-insensitive on Windows) before re-inserting at the front.
        Settings.RecentRepositories.RemoveAll(r =>
            string.Equals(NormalizePath(r.Path), normalized, StringComparison.OrdinalIgnoreCase));

        Settings.RecentRepositories.Insert(0, new RecentRepository
        {
            Path = normalized,
            Name = DeriveName(normalized),
            LastOpenedUtc = DateTimeOffset.UtcNow,
        });

        if (Settings.RecentRepositories.Count > MaxRecentRepositories)
        {
            Settings.RecentRepositories.RemoveRange(
                MaxRecentRepositories,
                Settings.RecentRepositories.Count - MaxRecentRepositories);
        }

        Save();
    }

    public void RemoveRecentRepository(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            return;

        var normalized = NormalizePath(repositoryPath);
        var removed = Settings.RecentRepositories.RemoveAll(r =>
            string.Equals(NormalizePath(r.Path), normalized, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
            Save();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private static string DeriveName(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
