namespace Webora.Application.Parking;

/// <summary>A user's home address and the resulting commute distance to the lot.</summary>
public sealed record UserHomeDto(string? Address, double? DistanceKm, bool Verified);

/// <summary>Manages a user's home address: geocoding it and computing the commute distance.</summary>
public interface IUserLocationService
{
    Task<UserHomeDto> GetHomeAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Geocodes the address, computes the distance to the lot and stores both on the user.</summary>
    Task<ParkingResult> SetHomeAddressAsync(Guid userId, string? address, CancellationToken cancellationToken = default);

    /// <summary>Admin action: mark a user's home address as verified (or revoke it).</summary>
    Task<ParkingResult> SetHomeVerifiedAsync(Guid userId, bool verified, CancellationToken cancellationToken = default);
}
