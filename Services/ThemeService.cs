using System;
using Avalonia;
using Avalonia.Styling;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Drives the Avalonia <see cref="Application.RequestedThemeVariant"/> from the
/// selected <see cref="ThemeMode"/> and keeps the syntax-highlight theme in sync.
/// System mode maps to <see cref="ThemeVariant.Default"/>, which follows the OS and
/// raises <see cref="Application.ActualThemeVariantChanged"/> when it changes.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settings;
    private readonly ISyntaxHighlighter _highlighter;
    private bool? _lastIsDark;
    private bool _subscribed;

    public ThemeService(ISettingsService settings, ISyntaxHighlighter highlighter)
    {
        _settings = settings;
        _highlighter = highlighter;
        Mode = settings.Settings.ThemeMode;
    }

    public ThemeMode Mode { get; private set; }

    public event Action? EffectiveThemeChanged;

    public void Initialize()
    {
        var app = Application.Current;
        if (app is null)
            return;

        if (!_subscribed)
        {
            app.ActualThemeVariantChanged += (_, _) => SyncEffective();
            _subscribed = true;
        }

        ApplyMode(Mode);
    }

    public void SetMode(ThemeMode mode)
    {
        Mode = mode;
        _settings.Settings.ThemeMode = mode;
        _settings.Save();
        ApplyMode(mode);
    }

    private void ApplyMode(ThemeMode mode)
    {
        var app = Application.Current;
        if (app is null)
            return;

        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        SyncEffective();
    }

    /// <summary>Pushes the resolved variant to the highlighter and notifies listeners (once per change).</summary>
    private void SyncEffective()
    {
        var app = Application.Current;
        if (app is null)
            return;

        var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
        if (_lastIsDark == isDark)
            return;

        _lastIsDark = isDark;
        _highlighter.SetDark(isDark);
        EffectiveThemeChanged?.Invoke();
    }
}
