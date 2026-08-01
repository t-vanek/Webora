using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking.Maps;

/// <summary>
/// One object drawn on a <see cref="LotMap"/>: a stall, a building footprint, a driving lane or a
/// caption. Geometry is a <see cref="MapRect"/> flattened into columns, so a shape is one row and
/// moving it is one UPDATE.
/// </summary>
public class MapShape : Entity
{
    public const int MaxLabelLength = 64;

    public Guid LotMapId { get; private set; }

    public MapShapeKind Kind { get; private set; }

    /// <summary>
    /// The text printed on the plan — "434", "H14", "VISITOR", "ADMINISTRATIVNÍ BUDOVA č.2". It is
    /// what the shape says, not what it is bound to: a stall keeps its printed number whether or not
    /// this company rents it, and that is exactly what auto-linking matches spot codes against.
    /// </summary>
    public string? Label { get; private set; }

    public double X { get; private set; }

    public double Y { get; private set; }

    public double Width { get; private set; }

    public double Height { get; private set; }

    /// <summary>Degrees clockwise about the shape's own centre.</summary>
    public double Rotation { get; private set; }

    /// <summary>
    /// The spot this shape draws, or null when it draws a stall the company does not rent. Null is the
    /// normal state for most of a site plan and is not a defect: the shape is context. Linking is by
    /// id rather than by code so renaming a spot cannot silently detach it from its rectangle.
    /// </summary>
    public Guid? ParkingSpotId { get; private set; }

    public bool IsLinked => ParkingSpotId is not null;

    public MapRect Rect => new(X, Y, Width, Height, Rotation);

    private MapShape() { }

    public MapShape(Guid lotMapId, MapShapeKind kind, MapRect rect, string? label = null)
    {
        LotMapId = lotMapId;
        Kind = kind;
        SetRect(rect);
        Relabel(label);
    }

    public void SetRect(MapRect rect)
    {
        var sane = rect.Sanitized();
        if (!sane.IsValid())
        {
            throw new ArgumentException("Shape geometry is out of range.", nameof(rect));
        }

        (X, Y, Width, Height, Rotation) = (sane.X, sane.Y, sane.Width, sane.Height, sane.Rotation);
    }

    public void Relabel(string? label)
    {
        var trimmed = label?.Trim();
        Label = string.IsNullOrEmpty(trimmed)
            ? null
            : trimmed[..Math.Min(trimmed.Length, MaxLabelLength)];
    }

    public void ChangeKind(MapShapeKind kind)
    {
        Kind = kind;
        // Only a stall can stand for a bookable spot; turning one into a building would otherwise
        // leave a live link on a shape the board no longer draws as clickable.
        if (kind != MapShapeKind.Spot)
        {
            ParkingSpotId = null;
        }
    }

    /// <summary>Binds the shape to a spot, or clears the binding when passed null.</summary>
    public void LinkSpot(Guid? parkingSpotId)
    {
        if (parkingSpotId is not null && Kind != MapShapeKind.Spot)
        {
            throw new InvalidOperationException("Only a spot shape can be linked to a parking spot.");
        }

        ParkingSpotId = parkingSpotId;
    }
}
