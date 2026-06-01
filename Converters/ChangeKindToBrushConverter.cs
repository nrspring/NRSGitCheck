using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using NRSGitCheck.Models;

namespace NRSGitCheck.Converters;

/// <summary>Maps a <see cref="ChangeKind"/> to the colored badge brush (FR-13).</summary>
public sealed class ChangeKindToBrushConverter : IValueConverter
{
    public static readonly ChangeKindToBrushConverter Instance = new();

    private static readonly IBrush Added = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#E0A030"));
    private static readonly IBrush Deleted = new SolidColorBrush(Color.Parse("#E05252"));
    private static readonly IBrush Renamed = new SolidColorBrush(Color.Parse("#3D8BFD"));
    private static readonly IBrush Untracked = new SolidColorBrush(Color.Parse("#26A69A"));
    private static readonly IBrush Fallback = new SolidColorBrush(Color.Parse("#888888"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ChangeKind.Added => Added,
        ChangeKind.Modified => Modified,
        ChangeKind.Deleted => Deleted,
        ChangeKind.Renamed => Renamed,
        ChangeKind.Untracked => Untracked,
        _ => Fallback,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
