using System;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Applies and persists the app's light/dark appearance (FR-28..31). In System
/// mode the effective variant follows the OS and updates live.
/// </summary>
public interface IThemeService
{
    /// <summary>The selected mode (System/Light/Dark).</summary>
    ThemeMode Mode { get; }

    /// <summary>Applies the saved mode and starts tracking OS theme changes. Call once at startup.</summary>
    void Initialize();

    /// <summary>Switches mode, persists it, and applies it (FR-30, FR-31).</summary>
    void SetMode(ThemeMode mode);

    /// <summary>Raised when the effective (resolved) light/dark variant actually changes.</summary>
    event Action? EffectiveThemeChanged;
}
