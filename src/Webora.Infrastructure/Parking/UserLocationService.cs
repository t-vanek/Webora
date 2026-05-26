using Microsoft.EntityFrameworkCore;
using Webora.Application.Parking;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Parking;

public sealed class UserLocationService(
    IDbContextFactory<WeboraDbContext> dbContextFactory,
    IGeocoder geocoder,
    IDistanceProvider distanceProvider,
    IParkingSettingsService parkingSettings) : IUserLocationService
{
    public async Task<UserHomeDto> GetHomeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserHomeDto(u.HomeAddress, u.CommuteDistanceKm))
            .FirstOrDefaultAsync(cancellationToken);
        return user ?? new UserHomeDto(null, null);
    }

    public async Task<ParkingResult> SetHomeAddressAsync(Guid userId, string? address, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return ParkingResult.Failure("Parking_Error_UserNotFound");
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            user.HomeAddress = null;
            user.HomeLatitude = null;
            user.HomeLongitude = null;
            user.CommuteDistanceKm = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            return ParkingResult.Success;
        }

        var point = await geocoder.GeocodeAsync(address, cancellationToken);
        if (point is not { } home)
        {
            return ParkingResult.Failure("Parking_Error_GeocodeFailed");
        }

        user.HomeAddress = address.Trim();
        user.HomeLatitude = home.Latitude;
        user.HomeLongitude = home.Longitude;

        var lot = await parkingSettings.GetLotLocationAsync(cancellationToken);
        user.CommuteDistanceKm = lot is { } lotPoint
            ? await distanceProvider.DistanceKmAsync(home, lotPoint, cancellationToken)
            : null;

        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }
}
