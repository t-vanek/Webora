namespace D3Parking.Domain.Parking;

/// <summary>
/// What kind of vehicle a fleet-registry entry describes, which decides the parking entitlement
/// its pairing carries: a company vehicle may hold a dedicated (resident) spot on top of ordinary
/// reservations, an employee's own vehicle books from the shared pool only.
/// </summary>
public enum VehicleType
{
    /// <summary>A company vehicle — may be assigned a dedicated spot that pairing materializes.</summary>
    Company = 0,

    /// <summary>An employee's own vehicle — reservations only, never an assigned spot.</summary>
    Employee = 1,
}
