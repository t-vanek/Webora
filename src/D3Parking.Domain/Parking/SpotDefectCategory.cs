namespace D3Parking.Domain.Parking;

/// <summary>
/// What kind of thing is wrong with the lot. A short, closed list on purpose: it is picked on a
/// phone by someone standing in the rain, and it is what the spot manager sorts by.
/// </summary>
public enum SpotDefectCategory
{
    /// <summary>Potholes, ice, broken kerbs — the ground itself.</summary>
    Surface,

    /// <summary>Faded or missing lines, an unreadable spot code.</summary>
    Markings,

    /// <summary>The barrier, gate or reader at the entrance.</summary>
    Access,

    /// <summary>Lighting, out or broken.</summary>
    Lighting,

    /// <summary>Something in the way that is not a car: a skip, a pallet, building materials.</summary>
    Obstruction,

    Other,
}
