namespace Webora.Application.Parking;

/// <summary>A user's home address and the resulting commute distance to the lot.</summary>
public sealed record UserHomeDto(string? Address, double? DistanceKm);

/// <summary>Manages a user's home address: geocoding it and computing the commute distance.</summary>
public interface IUserLocationService
{
    Task<UserHomeDto> GetHomeAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Geocodes the address, computes the distance to the lot and stores both on the user.</summary>
    Task<ParkingResult> SetHomeAddressAsync(Guid userId, string? address, CancellationToken cancellationToken = default);
}
