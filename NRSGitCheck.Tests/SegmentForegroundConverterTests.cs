using System.Globalization;
using Avalonia.Media;
using NRSGitCheck.Converters;
using NRSGitCheck.Models;
using NRSGitCheck.ViewModels;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Guards the regression where uncolored segments (punctuation, whitespace) rendered
/// invisibly: the converter must always yield a real brush, never an unset/null value.
/// </summary>
public sealed class SegmentForegroundConverterTests
{
    [Fact]
    public void Segment_without_syntax_color_still_gets_a_brush()
    {
        var segment = new RenderSegment("{", WordSegmentKind.Unchanged, foreground: null);

        var result = SegmentForegroundConverter.Instance.Convert(
            segment, typeof(IBrush), null, CultureInfo.InvariantCulture);

        Assert.IsAssignableFrom<IBrush>(result);
    }

    [Fact]
    public void Segment_with_syntax_color_gets_that_color()
    {
        var segment = new RenderSegment("if", WordSegmentKind.Unchanged, "#C586C0");

        var result = SegmentForegroundConverter.Instance.Convert(
            segment, typeof(IBrush), null, CultureInfo.InvariantCulture);

        var brush = Assert.IsType<SolidColorBrush>(result);
        Assert.Equal(Color.Parse("#C586C0"), brush.Color);
    }
}
