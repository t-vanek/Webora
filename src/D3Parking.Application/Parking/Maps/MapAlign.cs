using D3Parking.Domain.Parking.Maps;

namespace D3Parking.Application.Parking.Maps;

/// <summary>How a selection is to be lined up.</summary>
public enum MapAlignment
{
    Left,
    CentreX,
    Right,
    Top,
    MiddleY,
    Bottom,

    /// <summary>Even horizontal spacing between the outermost two, which stay put.</summary>
    DistributeX,

    /// <summary>Even vertical spacing between the outermost two, which stay put.</summary>
    DistributeY,
}

/// <summary>
/// Lines a selection up. Pure, and it only ever produces new positions — the editor sends them down
/// the same path a drag takes, so alignment inherits the clamping and the undo step for free rather
/// than needing its own of either.
/// </summary>
/// <remarks>
/// Everything is measured on the axis-aligned bounding box, so a stall traced at an angle lines up by
/// what it visually occupies. Shapes are moved and never resized: a row that has been aligned is
/// still the row that was drawn.
/// </remarks>
public static class MapAlign
{
    public static IReadOnlyList<(T Shape, MapRect Rect)> Apply<T>(
        IReadOnlyList<T> shapes,
        Func<T, MapRect> geometry,
        MapAlignment mode)
    {
        if (shapes.Count < 2)
        {
            return [];
        }

        var placed = shapes
            .Select(shape =>
            {
                var rect = geometry(shape);
                var (minX, minY, maxX, maxY) = rect.Bounds();
                return (Shape: shape, Rect: rect, MinX: minX, MinY: minY, MaxX: maxX, MaxY: maxY);
            })
            .ToList();

        // The selection's own extents, measured once rather than per shape.
        var (left, right) = (placed.Min(p => p.MinX), placed.Max(p => p.MaxX));
        var (top, bottom) = (placed.Min(p => p.MinY), placed.Max(p => p.MaxY));

        return mode switch
        {
            MapAlignment.Left => Shift(placed, p => left - p.MinX, _ => 0),
            MapAlignment.Right => Shift(placed, p => right - p.MaxX, _ => 0),
            MapAlignment.CentreX => Centre(placed, horizontal: true),
            MapAlignment.Top => Shift(placed, _ => 0, p => top - p.MinY),
            MapAlignment.Bottom => Shift(placed, _ => 0, p => bottom - p.MaxY),
            MapAlignment.MiddleY => Centre(placed, horizontal: false),
            MapAlignment.DistributeX => Distribute(placed, horizontal: true),
            _ => Distribute(placed, horizontal: false),
        };
    }

    private static IReadOnlyList<(T, MapRect)> Shift<T>(
        List<(T Shape, MapRect Rect, double MinX, double MinY, double MaxX, double MaxY)> placed,
        Func<(T Shape, MapRect Rect, double MinX, double MinY, double MaxX, double MaxY), double> dx,
        Func<(T Shape, MapRect Rect, double MinX, double MinY, double MaxX, double MaxY), double> dy) =>
        placed
            .Select(p => (p.Shape, Rect: p.Rect.MovedBy(dx(p), dy(p)).Sanitized()))
            .ToList();

    private static IReadOnlyList<(T, MapRect)> Centre<T>(
        List<(T Shape, MapRect Rect, double MinX, double MinY, double MaxX, double MaxY)> placed,
        bool horizontal)
    {
        // The centre of what the selection occupies, not the average of the shapes' own centres —
        // otherwise a cluster of small shapes drags the line towards itself.
        var target = horizontal
            ? (placed.Min(p => p.MinX) + placed.Max(p => p.MaxX)) / 2
            : (placed.Min(p => p.MinY) + placed.Max(p => p.MaxY)) / 2;

        return placed
            .Select(p =>
            {
                var own = horizontal ? (p.MinX + p.MaxX) / 2 : (p.MinY + p.MaxY) / 2;
                var delta = target - own;
                return (p.Shape, horizontal ? p.Rect.MovedBy(delta, 0).Sanitized() : p.Rect.MovedBy(0, delta).Sanitized());
            })
            .ToList();
    }

    private static IReadOnlyList<(T, MapRect)> Distribute<T>(
        List<(T Shape, MapRect Rect, double MinX, double MinY, double MaxX, double MaxY)> placed,
        bool horizontal)
    {
        // Two shapes are already evenly spaced by definition; there is nothing in between to place.
        if (placed.Count < 3)
        {
            return [];
        }

        double CentreOf((T Shape, MapRect Rect, double MinX, double MinY, double MaxX, double MaxY) p) =>
            horizontal ? (p.MinX + p.MaxX) / 2 : (p.MinY + p.MaxY) / 2;

        var ordered = placed.OrderBy(CentreOf).ToList();
        var first = CentreOf(ordered[0]);
        var last = CentreOf(ordered[^1]);
        var step = (last - first) / (ordered.Count - 1);

        var moved = new List<(T, MapRect)>(ordered.Count - 2);
        for (var i = 1; i < ordered.Count - 1; i++)
        {
            var delta = (first + (step * i)) - CentreOf(ordered[i]);
            var rect = ordered[i].Rect;
            moved.Add((ordered[i].Shape, horizontal ? rect.MovedBy(delta, 0).Sanitized() : rect.MovedBy(0, delta).Sanitized()));
        }

        return moved;
    }
}
