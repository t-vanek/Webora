namespace D3Parking.Application.Parking;

/// <summary>A fleet vehicle as the administration lists it.</summary>
public sealed record CompanyVehicleDto(
    Guid Id,
    string Plate,
    string? Name,
    string? DriverEmail,
    Guid? AssignedSpotId,
    string? SpotCode,
    Guid? PairedUserId,
    string? PairedUserName,
    bool IsActive,
    string? Notes);

/// <summary>Where the signed-in user stands with respect to the fleet registry.</summary>
public enum VehiclePairingState
{
    /// <summary>No license plate in the profile.</summary>
    NoPlate,

    /// <summary>The plate matches no active fleet vehicle.</summary>
    NoMatch,

    /// <summary>
    /// The plate matches a vehicle, but self-service pairing is not available: the registry has
    /// no driver email, the email differs from the account's, or the vehicle is paired to
    /// someone else. The user is told to contact an administrator (never which of those it is —
    /// the registry's contents are not theirs to probe).
    /// </summary>
    NotPairable,

    /// <summary>All checks pass; the user may request and confirm the pairing code.</summary>
    CodeRequired,

    /// <summary>This user is paired with the vehicle.</summary>
    Paired,
}

/// <summary>Pairing status for the profile page.</summary>
public sealed record PairingStatusDto(
    VehiclePairingState State,
    string? VehiclePlate,
    string? VehicleName,
    string? SpotCode,
    DateTimeOffset? CodeSentAtUtc);
