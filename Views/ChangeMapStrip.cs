using System;
using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NRSGitCheck.Converters;
using NRSGitCheck.ViewModels;

namespace NRSGitCheck.Views;

/// <summary>
/// The overview lane beside a diff pane's scrollbar: every changed region of the
/// file drawn as a tick at its position in the document, so the shape of the change
/// set is visible without scrolling. Clicking a tick jumps the pane to it.
/// </summary>
public sealed class ChangeMapStrip : Control
{
    /// <summary>The rendered rows of the pane this strip summarizes.</summary>
    public static readonly StyledProperty<IEnumerable?> RowsProperty =
        AvaloniaProperty.Register<ChangeMapStrip, IEnumerable?>(nameof(Rows));

    /// <summary>The list to scroll when a tick is clicked.</summary>
    public static readonly StyledProperty<ListBox?> TargetProperty =
        AvaloniaProperty.Register<ChangeMapStrip, ListBox?>(nameof(Target));

    public IEnumerable? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public ListBox? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>Horizontal inset of a tick, leaving a hairline of lane on each side.</summary>
    private const double TickInset = 2;

    /// <summary>Shortest a tick may be drawn, so a one-line change in a long file still reads.</summary>
    private const double MinTickHeight = 3;

    private INotifyCollectionChanged? _observed;

    public ChangeMapStrip()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != RowsProperty)
            return;

        // Rows stream in hunk by hunk, so follow the collection rather than
        // snapshotting it once when the file is selected.
        if (_observed is not null)
            _observed.CollectionChanged -= OnRowsChanged;

        _observed = change.NewValue as INotifyCollectionChanged;

        if (_observed is not null)
            _observed.CollectionChanged += OnRowsChanged;

        InvalidateVisual();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        // The lane is drawn even when empty: it gives the strip a hit-test surface
        // and shows the scrollbar has a companion rather than a rendering gap.
        context.FillRectangle(DiffPalette.MapLane, new Rect(0, 0, width, height));

        var overview = ChangeMarkers.Build(Rows);
        if (overview.RowCount == 0)
            return;

        var scale = height / overview.RowCount;
        var tickWidth = Math.Max(1, width - (TickInset * 2));

        foreach (var marker in overview.Markers)
        {
            var tickHeight = Math.Max(MinTickHeight, (marker.End - marker.Start + 1) * scale);
            // A short tick at the very bottom would otherwise be drawn past the edge.
            var top = Math.Min(marker.Start * scale, Math.Max(0, height - tickHeight));

            if (marker.Kind == ChangeMarkerKind.Mixed)
            {
                // A replaced block: removed on the left half, added on the right,
                // matching which pane each side of the change lives in.
                var half = tickWidth / 2;
                context.FillRectangle(DiffPalette.RemovedMarker, new Rect(TickInset, top, half, tickHeight));
                context.FillRectangle(DiffPalette.AddedMarker, new Rect(TickInset + half, top, half, tickHeight));
                continue;
            }

            var brush = marker.Kind == ChangeMarkerKind.Added ? DiffPalette.AddedMarker : DiffPalette.RemovedMarker;
            context.FillRectangle(brush, new Rect(TickInset, top, tickWidth, tickHeight));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Target is not { } list || Bounds.Height <= 0)
            return;

        var overview = ChangeMarkers.Build(Rows);
        if (overview.RowCount == 0)
            return;

        var fraction = e.GetPosition(this).Y / Bounds.Height;
        var index = Math.Clamp((int)(fraction * overview.RowCount), 0, overview.RowCount - 1);

        // Reveal a little past the target first so the clicked region lands with room
        // beneath it, then the region itself — the same two-step the keyboard uses.
        list.ScrollIntoView(DiffViewModel.TrailingContextRow(index, overview.RowCount));
        list.ScrollIntoView(index);
        e.Handled = true;
    }
}
