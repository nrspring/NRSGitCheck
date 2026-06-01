using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NRSGitCheck.Models;
using NRSGitCheck.ViewModels;

namespace NRSGitCheck.Converters;

/// <summary>Shared diff color palette (semi-transparent so it reads on light and dark).</summary>
internal static class DiffPalette
{
    public static readonly IBrush Transparent = Brushes.Transparent;
    public static readonly IBrush AddedLine = new SolidColorBrush(Color.Parse("#3328A745"));
    public static readonly IBrush RemovedLine = new SolidColorBrush(Color.Parse("#33E05252"));
    public static readonly IBrush EmptyCell = new SolidColorBrush(Color.Parse("#0A808080"));
    public static readonly IBrush AddedWord = new SolidColorBrush(Color.Parse("#5528A745"));
    public static readonly IBrush RemovedWord = new SolidColorBrush(Color.Parse("#55E05252"));
}

/// <summary>Maps a <see cref="DiffLineKind"/> to the line's background tint (FR-18).</summary>
public sealed class DiffLineKindToBackgroundConverter : IValueConverter
{
    public static readonly DiffLineKindToBackgroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffLineKind.Added => DiffPalette.AddedLine,
        DiffLineKind.Removed => DiffPalette.RemovedLine,
        _ => DiffPalette.Transparent,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a side-by-side <see cref="SideCell"/> to its background.</summary>
public sealed class SideCellBackgroundConverter : IValueConverter
{
    public static readonly SideCellBackgroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SideCell cell)
            return DiffPalette.Transparent;
        if (cell.IsEmpty)
            return DiffPalette.EmptyCell;
        return cell.Kind switch
        {
            DiffLineKind.Added => DiffPalette.AddedLine,
            DiffLineKind.Removed => DiffPalette.RemovedLine,
            _ => DiffPalette.Transparent,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a <see cref="RenderSegment"/>'s hex foreground to a cached brush (FR-20).
/// When a segment has no syntax color (punctuation, whitespace, plain text) it
/// returns the app's theme foreground brush, so that text stays visible. Returning
/// <see cref="AvaloniaProperty.UnsetValue"/> here would leave the explicitly-bound
/// Foreground null (invisible) rather than inheriting the theme color.
/// </summary>
public sealed class SegmentForegroundConverter : IValueConverter
{
    public static readonly SegmentForegroundConverter Instance = new();

    private static readonly Dictionary<string, IBrush> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RenderSegment { Foreground: { } hex } || string.IsNullOrEmpty(hex))
            return DefaultForeground();

        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var brush))
                return brush;

            try
            {
                brush = new SolidColorBrush(Color.Parse(hex));
            }
            catch (FormatException)
            {
                return DefaultForeground();
            }

            Cache[hex] = brush;
            return brush;
        }
    }

    /// <summary>
    /// The current theme's default text foreground. The diff is rebuilt when the
    /// theme changes, so this is re-evaluated for the active variant each time.
    /// </summary>
    private static IBrush DefaultForeground()
    {
        var app = Application.Current;
        if (app is not null &&
            app.TryGetResource("SystemControlForegroundBaseHighBrush", app.ActualThemeVariant, out var res) &&
            res is IBrush brush)
            return brush;

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a <see cref="RenderSegment"/> to its word-level highlight brush (FR-23).</summary>
public sealed class SegmentBrushConverter : IValueConverter
{
    public static readonly SegmentBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RenderSegment seg)
            return DiffPalette.Transparent;
        return seg.Highlight switch
        {
            WordSegmentKind.Added => DiffPalette.AddedWord,
            WordSegmentKind.Removed => DiffPalette.RemovedWord,
            _ => DiffPalette.Transparent,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
