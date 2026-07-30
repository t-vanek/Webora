using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>A fleet vehicle as the administration lists it.</summary>
public sealed record CompanyVehicleDto(
    Guid Id,
    string Plate,
    VehicleType Type,
    string? Name,
    string? DriverEmail,
    Guid? AssignedSpotId,
    string? SpotCode,
    Guid? PairedUserId,
    string? PairedUserName,
    DateTimeOffset? PairedAtUtc,
    bool IsActive,
    string? Notes,
    FleetPairingState PairingState,
    DateTimeOffset? PairingCodeSentAtUtc,
    DateTimeOffset? PairingLockedUntilUtc);

/// <summary>
/// Where a vehicle stands on the way to being paired, as the administration sees it. The
/// user-facing <see cref="VehiclePairingState"/> is deliberately opaque about why pairing is
/// unavailable; this one is the opposite — the admin owns the registry, so the grid names the
/// exact missing ingredient instead of leaving them to reconstruct it from support mails.
/// </summary>
public enum FleetPairingState
{
    /// <summary>Deactivated (returned lease); kept for history, never pairs.</summary>
    Inactive,

    /// <summary>No driver email on file (pool car): only an administrator can pair it.</summary>
    ManualOnly,

    /// <summary>Driver email is set, but no active account with a confirmed email uses it yet.</summary>
    NoAccount,

    /// <summary>The driver's account exists, but its profile plate does not match the vehicle.</summary>
    PlateMissing,

    /// <summary>All three factors line up; waiting for the driver to request the code.</summary>
    ReadyToPair,

    /// <summary>A confirmation code is out, waiting to be typed back.</summary>
    CodeSent,

    /// <summary>Too many failed confirmations; self-service is paused until the window passes.</summary>
    Locked,

    /// <summary>Paired with an account (see <see cref="CompanyVehicleDto.PairedUserId"/>).</summary>
    Paired,
}

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
    // Null until a concrete vehicle is matched (NoPlate/NoMatch/NotPairable) — those states
    // deliberately reveal nothing about what the registry knows.
    VehicleType? VehicleType,
    string? SpotCode,
    DateTimeOffset? CodeSentAtUtc);
