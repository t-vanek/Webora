using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// A company-fleet vehicle, identified by its license plate. The fleet registry is the bridge
/// between the fleet manager's world (cars, assigned parking spots) and user accounts: when a
/// user's profile plate matches a vehicle whose registered driver email equals the account's
/// email, the user may pair with the vehicle by confirming an emailed code — and pairing with a
/// vehicle that has an assigned spot makes them that spot's resident.
/// </summary>
public class CompanyVehicle : Entity
{
    /// <summary>The plate as the fleet manager typed it (for display).</summary>
    public string Plate { get; private set; } = string.Empty;

    /// <summary>Canonical form (see <see cref="PlateNormalizer"/>); unique across the fleet.</summary>
    public string NormalizedPlate { get; private set; } = string.Empty;

    /// <summary>Optional label ("Škoda Octavia — sales").</summary>
    public string? Name { get; private set; }

    /// <summary>
    /// Email of the vehicle's registered driver. Self-service pairing requires it to match the
    /// claiming account's email exactly — the plate alone is public information readable off the
    /// car, so it must never be sufficient on its own. Null means the vehicle (a pool car, an
    /// unfilled record) cannot be self-paired; an administrator can still pair it manually.
    /// </summary>
    public string? DriverEmail { get; private set; }

    /// <summary>The parking spot this vehicle parks on; pairing materializes it as residency.</summary>
    public Guid? AssignedSpotId { get; private set; }

    public Guid? PairedUserId { get; private set; }

    public DateTimeOffset? PairedAtUtc { get; private set; }

    /// <summary>Inactive vehicles (returned lease) stay for history but never pair.</summary>
    public bool IsActive { get; private set; } = true;

    public string? Notes { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    // Pairing-code guardrails. The code itself is a stateless Rfc6238 token (nothing stored);
    // what must be stored is how often codes are sent and how many confirmations failed, so the
    // 6-digit space cannot be brute-forced and the mailbox cannot be spammed.
    public DateTimeOffset? PairingCodeSentAtUtc { get; private set; }

    public int PairingAttempts { get; private set; }

    public DateTimeOffset? PairingAttemptsWindowStartUtc { get; private set; }

    private CompanyVehicle() { }

    public CompanyVehicle(string plate, string? name, string? driverEmail, Guid? assignedSpotId, string? notes, DateTimeOffset createdAtUtc)
    {
        Rename(plate);
        Name = name;
        DriverEmail = driverEmail;
        AssignedSpotId = assignedSpotId;
        Notes = notes;
        CreatedAtUtc = createdAtUtc;
    }

    public void Rename(string plate)
    {
        var normalized = PlateNormalizer.Normalize(plate);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A license plate must contain at least one letter or digit.", nameof(plate));
        }

        Plate = plate.Trim();
        NormalizedPlate = normalized;
    }

    public void Update(string? name, string? driverEmail, string? notes)
    {
        Name = name;
        DriverEmail = driverEmail;
        Notes = notes;
    }

    public void AssignSpot(Guid? spotId) => AssignedSpotId = spotId;

    public void Pair(Guid userId, DateTimeOffset at)
    {
        PairedUserId = userId;
        PairedAtUtc = at;
        ResetPairingGuards();
    }

    public void Unpair()
    {
        PairedUserId = null;
        PairedAtUtc = null;
        ResetPairingGuards();
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    public void RegisterCodeSent(DateTimeOffset at) => PairingCodeSentAtUtc = at;

    /// <summary>Counts a failed confirmation within a rolling window; returns the new count.</summary>
    public int RegisterFailedAttempt(DateTimeOffset at, TimeSpan window)
    {
        if (PairingAttemptsWindowStartUtc is not { } start || at - start > window)
        {
            PairingAttemptsWindowStartUtc = at;
            PairingAttempts = 0;
        }

        return ++PairingAttempts;
    }

    private void ResetPairingGuards()
    {
        PairingCodeSentAtUtc = null;
        PairingAttempts = 0;
        PairingAttemptsWindowStartUtc = null;
    }
}
