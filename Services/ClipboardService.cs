using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;   // SetTextAsync is an extension in Avalonia 12

namespace NRSGitCheck.Services;

/// <summary>Clipboard backed by the desktop main window's top level.</summary>
public sealed class ClipboardService : IClipboardService
{
    public async Task<bool> SetTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            return false;

        if (TopLevel.GetTopLevel(window)?.Clipboard is not { } clipboard)
            return false;

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception)
        {
            // Another application can hold the clipboard open; copying is not worth a crash.
            return false;
        }
    }
}
