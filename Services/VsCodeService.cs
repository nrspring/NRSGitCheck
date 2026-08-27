using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace NRSGitCheck.Services;

/// <summary>
/// Opens a folder in Visual Studio Code. The executable is looked for in the places
/// the official installers put it and then on <c>PATH</c>, rather than shelling out
/// to <c>code</c> and hoping: the launcher on PATH is a <c>.cmd</c> that flashes a
/// console window, and resolving <c>Code.exe</c> directly avoids that.
/// </summary>
public sealed class VsCodeService : IEditorService
{
    private readonly Lazy<string?> _executable;

    public VsCodeService()
        : this(File.Exists, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test seam: the filesystem and environment are the only machine state used.</summary>
    internal VsCodeService(Func<string, bool> fileExists, Func<string, string?> environment)
    {
        // Resolved once and remembered; a user who installs the editor mid-session can
        // restart, and the alternative is probing the disk on every status refresh.
        _executable = new Lazy<string?>(() => Locate(fileExists, environment));
    }

    public string Name => "Visual Studio Code";

    public bool IsAvailable => _executable.Value is not null;

    /// <summary>The resolved executable, for tests and diagnostics; null when not found.</summary>
    internal string? ExecutablePath => _executable.Value;

    public EditorResult Open(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return new EditorResult(false, "No folder to open.");

        if (!Directory.Exists(folder))
            return new EditorResult(false, "That folder is no longer on disk.");

        if (_executable.Value is not { } executable)
            return new EditorResult(false, $"{Name} was not found on this machine.");

        try
        {
            // A .cmd cannot be handed to CreateProcess directly, so route those through
            // cmd.exe with no window rather than falling back to ShellExecute, which
            // would flash a console.
            var start = IsScript(executable)
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", executable, folder } }
                : new ProcessStartInfo(executable) { ArgumentList = { folder } };

            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = folder;

            // The editor outlives this call; disposing the handle does not close it.
            using var process = Process.Start(start);
            return new EditorResult(true, $"Opened in {Name}.");
        }
        catch (Win32Exception)
        {
            return new EditorResult(false, $"Could not start {Name}.");
        }
        catch (Exception ex)
        {
            return new EditorResult(false, $"Could not start {Name}: {ex.Message}");
        }
    }

    private static bool IsScript(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first Visual Studio Code this machine has, or null. Install locations are
    /// checked before <c>PATH</c> so the windowless <c>Code.exe</c> wins over the
    /// <c>code.cmd</c> shim that sits beside it.
    /// </summary>
    internal static string? Locate(Func<string, bool> fileExists, Func<string, string?> environment)
    {
        foreach (var candidate in InstallCandidates(environment))
            if (fileExists(candidate))
                return candidate;

        return OnPath(fileExists, environment);
    }

    /// <summary>Where the user-scope and machine-scope installers put the editor.</summary>
    private static IEnumerable<string> InstallCandidates(Func<string, string?> environment)
    {
        string[] roots =
        {
            environment("LOCALAPPDATA") is { Length: > 0 } local ? Path.Combine(local, "Programs") : string.Empty,
            environment("ProgramFiles") ?? string.Empty,
            environment("ProgramFiles(x86)") ?? string.Empty,
        };

        foreach (var root in roots)
            if (root.Length > 0)
                yield return Path.Combine(root, "Microsoft VS Code", "Code.exe");
    }

    /// <summary>
    /// Catches installs this does not know about — Scoop, Chocolatey, a portable copy —
    /// by looking for the launcher the installer adds to <c>PATH</c>.
    /// </summary>
    private static string? OnPath(Func<string, bool> fileExists, Func<string, string?> environment)
    {
        if (environment("PATH") is not { Length: > 0 } path)
            return null;

        string[] names = { "Code.exe", "code.cmd", "code" };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;

            foreach (var name in names)
            {
                string full;
                try
                {
                    full = Path.Combine(trimmed, name);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not worth failing the whole lookup over.
                    break;
                }

                if (fileExists(full))
                    return full;
            }
        }

        return null;
    }
}
