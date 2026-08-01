using System.Globalization;

namespace D3Parking.Domain.Parking.Maps;

/// <summary>
/// A shape's geometry: an axis-aligned rectangle plus a rotation about its own centre, in the
/// map's own coordinate units (the SVG viewBox).
/// </summary>
/// <remarks>
/// A rectangle with an angle rather than a free polygon, because that is what a parking lot is
/// made of — every stall on a site plan is a rectangle, including the ones in a fanned-out arc,
/// which are simply rectangles at different angles. Five numbers instead of a variable-length
/// point list buys three things the editor lives on: eight resize handles that mean something,
/// exact arithmetic for repeating a stall along a row, and a shape that survives being nudged
/// without accumulating rounding drift in a dozen points. An irregular shape, if one ever turns
/// up, is a <see cref="Points"/> column added beside this — not a reason to model every stall as
/// a polygon today.
/// </remarks>
public readonly record struct MapRect(double X, double Y, double Width, double Height, double Rotation)
{
    /// <summary>
    /// Storage precision. Two decimals is finer than any lot is drawn and keeps a shape that has been
    /// dragged around from carrying a tail of float noise into the database and the export file.
    /// </summary>
    private const int Decimals = 2;

    /// <summary>Smallest shape the editor will keep — below this a drag was a misclick, not a rectangle.</summary>
    public const double MinSize = 1d;

    /// <summary>Guard rail on the coordinate space, so a runaway drag cannot write an absurd viewBox.</summary>
    public const double MaxCoordinate = 100_000d;

    public double CentreX => X + (Width / 2);

    public double CentreY => Y + (Height / 2);

    public bool IsRotated => Math.Abs(Rotation) > 0.001;

    /// <summary>
    /// The four corners in drawing order, with the rotation applied about the centre — what the
    /// renderer needs for an SVG polygon and what hit-testing needs for anything but an upright box.
    /// </summary>
    public (double X, double Y)[] Corners()
    {
        var (cx, cy) = (CentreX, CentreY);
        if (!IsRotated)
        {
            return [(X, Y), (X + Width, Y), (X + Width, Y + Height), (X, Y + Height)];
        }

        var radians = Rotation * Math.PI / 180d;
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));

        (double X, double Y) Rotate(double px, double py)
        {
            var (dx, dy) = (px - cx, py - cy);
            return (cx + (dx * cos) - (dy * sin), cy + (dx * sin) + (dy * cos));
        }

        return [Rotate(X, Y), Rotate(X + Width, Y), Rotate(X + Width, Y + Height), Rotate(X, Y + Height)];
    }

    /// <summary>The corners as an SVG <c>points</c> attribute, invariant-formatted so it parses in any culture.</summary>
    public string ToSvgPoints() =>
        string.Join(' ', Corners().Select(c =>
            string.Create(CultureInfo.InvariantCulture, $"{Round(c.X)},{Round(c.Y)}")));

    /// <summary>
    /// The axis-aligned box that contains the (possibly rotated) shape — the bounds a rubber-band
    /// selection tests against, and what "fit the map to its content" measures.
    /// </summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        var corners = Corners();
        return (corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    public MapRect MovedBy(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>
    /// The same rectangle slid back onto a map of the given size, measured by its bounding box so a
    /// rotated shape stays wholly visible. Position only — a shape is never resized to fit, and one
    /// larger than the map pins to the top-left corner and overflows rather than being squashed.
    /// </summary>
    /// <remarks>
    /// This is what stops work from being lost. Without it a shape dragged past the edge, or left
    /// outside after the map is made smaller, is off the canvas: it cannot be clicked, the zoom is
    /// bounded so it cannot be scrolled to, and nothing on screen says it is still there.
    /// </remarks>
    public MapRect ClampedInto(double width, double height)
    {
        var (minX, minY, maxX, maxY) = Bounds();

        var dx = maxX > width ? width - maxX : 0;
        if (minX + dx < 0)
        {
            dx = -minX;
        }

        var dy = maxY > height ? height - maxY : 0;
        if (minY + dy < 0)
        {
            dy = -minY;
        }

        return dx == 0 && dy == 0 ? this : MovedBy(dx, dy);
    }

    /// <summary>
    /// The same rectangle with a non-negative width and height. A drag that went up and to the left
    /// produces negative extents, and every consumer downstream — corners, bounds, the renderer —
    /// would otherwise have to cope with an inside-out box.
    /// </summary>
    public MapRect Normalized()
    {
        var (x, w) = Width < 0 ? (X + Width, -Width) : (X, Width);
        var (y, h) = Height < 0 ? (Y + Height, -Height) : (Y, Height);
        return new MapRect(x, y, w, h, Rotation);
    }

    /// <summary>
    /// Normalized, rounded to storage precision, with the angle folded into [0, 360). Everything that
    /// enters the model goes through here, so a shape read back is exactly the shape that was written.
    /// </summary>
    public MapRect Sanitized()
    {
        var r = Normalized();
        var rotation = r.Rotation % 360d;
        if (rotation < 0)
        {
            rotation += 360d;
        }

        return new MapRect(Round(r.X), Round(r.Y), Round(r.Width), Round(r.Height), Round(rotation));
    }

    /// <summary>
    /// Whether the rectangle is one the model will accept: real extents, finite coordinates inside the
    /// guard rail. Checked on the way in rather than trusted, because these numbers arrive from a
    /// pointer drag in the browser.
    /// </summary>
    public bool IsValid() =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
        && double.IsFinite(Rotation)
        && Width >= MinSize && Height >= MinSize
        && Math.Abs(X) <= MaxCoordinate && Math.Abs(Y) <= MaxCoordinate
        && Width <= MaxCoordinate && Height <= MaxCoordinate;

    /// <summary>
    /// The point (dx, dy), expressed in the rectangle's own frame, as a world-space offset. This is
    /// what makes a row of stalls repeat <em>along the row</em> rather than due east: a stall drawn at
    /// 30° steps 30° too.
    /// </summary>
    public (double X, double Y) LocalOffset(double dx, double dy)
    {
        if (!IsRotated)
        {
            return (dx, dy);
        }

        var radians = Rotation * Math.PI / 180d;
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
        return ((dx * cos) - (dy * sin), (dx * sin) + (dy * cos));
    }

    private static double Round(double value) => Math.Round(value, Decimals, MidpointRounding.AwayFromZero);
}
