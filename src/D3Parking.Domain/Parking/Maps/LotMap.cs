using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking.Maps;

/// <summary>
/// One drawing of a parking area: a coordinate space, an optional scan of the official site plan to
/// trace over, and the shapes that make it up (held as their own rows, not as a collection here —
/// dragging one stall must not load five hundred).
/// </summary>
/// <remarks>
/// A map is deliberately not the same thing as the lot. The drawing covers the whole site including
/// stalls that belong to other tenants, because a driver needs to see that spot 434 sits in the
/// middle of a row rather than alone in white space. Which of those shapes is actually bookable is
/// decided by <see cref="MapShape.ParkingSpotId"/>, one link at a time.
/// </remarks>
public class LotMap : Entity
{
    public const int MinDimension = 100;

    /// <summary>Matches <see cref="MapRect.MaxCoordinate"/>: a shape must be able to sit anywhere on the map.</summary>
    public const int MaxDimension = 100_000;

    /// <summary>Upper bound for the traced-over site plan. 12 MB takes a detailed A1 scan.</summary>
    public const int MaxBackgroundBytes = 12 * 1024 * 1024;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Width of the coordinate space (the SVG viewBox), in map units.</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>
    /// Editor grid pitch in map units; 0 disables snapping. Stored on the map rather than in the
    /// browser because it is a property of this drawing's scale — a plan where a stall is 40 units
    /// wide wants a different grid from one where it is 5.
    /// </summary>
    public int GridSize { get; private set; } = 5;

    /// <summary>
    /// Whether anything outside the editor may draw this map. Unpublished is the working state: a map
    /// half-traced is worse than no map, so the driver-facing screens must not pick one up by accident.
    /// </summary>
    public bool IsPublished { get; private set; }

    /// <summary>The scan of the official plan, shown under the shapes while tracing. Null once traced.</summary>
    public byte[]? Background { get; private set; }

    public string? BackgroundContentType { get; private set; }

    /// <summary>How strongly the underlay shows through, 0–100. An editor aid, not part of the output.</summary>
    public int BackgroundOpacity { get; private set; } = 50;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool HasBackground => Background is { Length: > 0 };

    private LotMap() { }

    public LotMap(string name, int width, int height, DateTimeOffset nowUtc)
    {
        Rename(name);
        Resize(width, height);
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Map name must not be empty.", nameof(name));
        }

        Name = name.Trim();
    }

    public void Resize(int width, int height)
    {
        Width = Math.Clamp(width, MinDimension, MaxDimension);
        Height = Math.Clamp(height, MinDimension, MaxDimension);
    }

    public void SetGridSize(int gridSize) => GridSize = Math.Clamp(gridSize, 0, 500);

    public void SetBackground(byte[] content, string contentType)
    {
        Background = content;
        BackgroundContentType = contentType;
    }

    public void ClearBackground()
    {
        Background = null;
        BackgroundContentType = null;
    }

    public void SetBackgroundOpacity(int opacity) => BackgroundOpacity = Math.Clamp(opacity, 0, 100);

    public void Publish() => IsPublished = true;

    public void Unpublish() => IsPublished = false;

    public void Touch(DateTimeOffset nowUtc) => UpdatedAtUtc = nowUtc;
}
