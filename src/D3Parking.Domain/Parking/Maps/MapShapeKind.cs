namespace D3Parking.Domain.Parking.Maps;

/// <summary>
/// What a drawn shape represents. Only a <see cref="Spot"/> can carry a spot link and be clicked
/// on a live board; everything else is the context that makes the drawing readable as a place —
/// without the buildings and the kerbs, a row of stalls is a row of boxes floating in nothing.
/// </summary>
public enum MapShapeKind
{
    /// <summary>A parking stall. Linked to a <see cref="ParkingSpot"/>, or foreign (someone else's stall).</summary>
    Spot,

    /// <summary>A building footprint — drawn, never bookable.</summary>
    Building,

    /// <summary>A driving lane, kerb or verge: shading that tells the driver where the road is.</summary>
    Aisle,

    /// <summary>Free-standing text (a building name, "PŘÍJEZD", a section caption).</summary>
    Label,
}
