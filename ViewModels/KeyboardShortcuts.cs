using System.Collections.Generic;

namespace NRSGitCheck.ViewModels;

/// <summary>An action paired with its displayed key gesture (FR-26).</summary>
public sealed record ShortcutInfo(string Action, string Keys);

/// <summary>
/// Single source of truth for the keyboard shortcuts shown in the UI (status-bar
/// hint and help overlay). The actual key handling in the window mirrors this
/// table (FR-24..27, defaults per Requirements §4.5).
/// </summary>
public static class KeyboardShortcuts
{
    public static IReadOnlyList<ShortcutInfo> All { get; } = new[]
    {
        new ShortcutInfo("Next change / hunk", "J  ·  Alt+↓"),
        new ShortcutInfo("Previous change / hunk", "K  ·  Alt+↑"),
        new ShortcutInfo("Next file", "Ctrl+↓  ·  ]"),
        new ShortcutInfo("Previous file", "Ctrl+↑  ·  ["),
        new ShortcutInfo("Toggle diff layout", "Ctrl+L"),
        new ShortcutInfo("Toggle theme", "Ctrl+T"),
        new ShortcutInfo("Open repository", "Ctrl+O"),
        new ShortcutInfo("Refresh changes", "F5"),
        new ShortcutInfo("Focus file filter", "Ctrl+F"),
        new ShortcutInfo("Show shortcuts", "?  ·  F1"),
    };

    /// <summary>Condensed reminder shown persistently in the status bar (FR-26).</summary>
    public const string StatusHint = "J / K change   ·   Ctrl+↑/↓ file   ·   Ctrl+L layout   ·   ? help";
}

/// <summary>Where to land within a file's hunks when navigating across file boundaries (FR-27).</summary>
public enum HunkPosition
{
    First,
    Last,
}
