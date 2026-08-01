using D3Parking.Domain.Parking.Maps;

namespace D3Parking.Application.Parking.Maps;

/// <summary>Which way a repeated row marches, read in the source shape's own frame.</summary>
public enum RowDirection
{
    Right,
    Left,
    Down,
    Up,
}

/// <summary>One shape a row plan would produce.</summary>
public sealed record PlannedShape(MapRect Rect, string? Label);

/// <summary>The expansion of a row, or the error key that says why the request was unusable.</summary>
public sealed record MapRowExpansion(IReadOnlyList<PlannedShape> Shapes, string? ErrorKey)
{
    public bool Succeeded => ErrorKey is null;

    public static MapRowExpansion Failure(string errorKey) => new([], errorKey);
}

/// <summary>
/// Repeats one traced stall into a row of them — the single tool that makes tracing a 460-stall site
/// plan an afternoon rather than a week. Pure, in the spirit of <see cref="SpotCodeSeries"/>: the
/// editor previews the numbering with the same call that later creates the shapes.
/// </summary>
public static class MapRowPlan
{
    /// <summary>Typo guard on one row. The longest row on a real plan is a few dozen.</summary>
    public const int MaxRowLength = 200;

    /// <summary>
    /// The whole row, source included at index 0, so a caller can render the preview and a caller
    /// creating shapes can skip the one that already exists. Steps along the source's own axis, so a
    /// stall drawn at an angle repeats along the row it belongs to instead of due east.
    /// </summary>
    /// <param name="count">Total stalls in the finished row, the source among them.</param>
    /// <param name="gap">Clear space between neighbours, in map units. Negative overlaps them.</param>
    /// <param name="step">Increment between consecutive labels; 1 for 428, 429, 430.</param>
    public static MapRowExpansion Expand(
        MapRect source,
        string? sourceLabel,
        int count,
        double gap,
        RowDirection direction,
        int step = 1)
    {
        var rect = source.Sanitized();
        if (!rect.IsValid())
        {
            return MapRowExpansion.Failure("Map_Error_ShapeGeometry");
        }

        if (count < 1 || count > MaxRowLength)
        {
            return MapRowExpansion.Failure("Map_Error_RowLength");
        }

        if (!double.IsFinite(gap))
        {
            return MapRowExpansion.Failure("Map_Error_RowGap");
        }

        // The pitch is measured across the edge the row advances over, so stalls meet edge to edge
        // with exactly `gap` between them however the rectangle is proportioned.
        var (localX, localY) = direction switch
        {
            RowDirection.Right => (rect.Width + gap, 0d),
            RowDirection.Left => (-(rect.Width + gap), 0d),
            RowDirection.Down => (0d, rect.Height + gap),
            _ => (0d, -(rect.Height + gap)),
        };

        var (stepX, stepY) = rect.LocalOffset(localX, localY);
        if (!double.IsFinite(stepX) || !double.IsFinite(stepY))
        {
            return MapRowExpansion.Failure("Map_Error_RowGap");
        }

        var shapes = new List<PlannedShape>(count);
        var label = sourceLabel?.Trim();
        for (var i = 0; i < count; i++)
        {
            var placed = rect.MovedBy(stepX * i, stepY * i).Sanitized();
            if (!placed.IsValid())
            {
                // The row has walked off the coordinate space; what fits is still a usable answer.
                return shapes.Count > 0
                    ? new MapRowExpansion(shapes, null)
                    : MapRowExpansion.Failure("Map_Error_ShapeGeometry");
            }

            shapes.Add(new PlannedShape(placed, label));
            label = MapLabelSequence.Next(label, step);
        }

        return new MapRowExpansion(shapes, null);
    }
}
