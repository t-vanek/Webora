using Microsoft.EntityFrameworkCore;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

/// <summary>Shared resolver for one physical resident entitlement per spot and local day.</summary>
internal static class ResidentAllocation
{
    public static async Task<HashSet<DateOnly>> AssignedDatesAsync(
        D3ParkingDbContext dbContext, ParkingSpot spot, Guid userId,
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken) =>
        (await AssignedUsersAsync(dbContext, spot, fromDate, toDate, cancellationToken))
            .Where(day => day.Value == userId)
            .Select(day => day.Key)
            .ToHashSet();

    /// <summary>
    /// Resolves the resident entitled to every physical day. The same map powers authorization and
    /// the named resident schedule, so what the UI says can never drift from what booking rules
    /// enforce.
    /// </summary>
    public static async Task<IReadOnlyDictionary<DateOnly, Guid>> AssignedUsersAsync(
        D3ParkingDbContext dbContext, ParkingSpot spot,
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        var residents = (await MembersAsync(dbContext, spot.Id, cancellationToken))
            .Where(r => r.RemovedAtUtc == null)
            .OrderBy(r => r.AssignedAtUtc).ThenBy(r => r.Id).ToList();

        if (residents.Count == 0)
        {
            return spot.OwnerId is { } ownerId
                ? AllDates(fromDate, toDate).ToDictionary(date => date, _ => ownerId)
                : new Dictionary<DateOnly, Guid>();
        }

        var residentById = residents.ToDictionary(r => r.Id, r => r.UserId);
        await dbContext.SpotDayAssignments
            .Where(a => a.SpotId == spot.Id && a.Date >= fromDate && a.Date <= toDate)
            .LoadAsync(cancellationToken);
        var overrides = dbContext.SpotDayAssignments.Local
            .Where(a => a.SpotId == spot.Id && a.Date >= fromDate && a.Date <= toDate
                && dbContext.Entry(a).State != EntityState.Deleted)
            .ToDictionary(a => a.Date, a => a.ResidentId);

        var assigned = new Dictionary<DateOnly, Guid>();
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            if (overrides.TryGetValue(date, out var residentId))
            {
                // A departed resident's existing booking keeps this physical day blocked. An
                // inactive membership never grants new booking rights to the departed person,
                // and the day must not silently rotate to another resident while it is occupied.
                if (residentById.TryGetValue(residentId, out var pinnedUser))
                {
                    assigned[date] = pinnedUser;
                }
                continue;
            }

            assigned[date] = residents[date.DayNumber % residents.Count].UserId;
        }

        return assigned;
    }

    // Read the tracked state as well as persisted rows: membership reconciliation runs before
    // SaveChanges in its caller's transaction, so additions/removals must take effect immediately.
    internal static async Task<List<ParkingSpotResident>> MembersAsync(
        D3ParkingDbContext dbContext, Guid spotId, CancellationToken cancellationToken)
    {
        await dbContext.ParkingSpotResidents.Where(r => r.SpotId == spotId).LoadAsync(cancellationToken);
        return dbContext.ParkingSpotResidents.Local
            .Where(r => r.SpotId == spotId && dbContext.Entry(r).State != EntityState.Deleted).ToList();
    }

    private static HashSet<DateOnly> AllDates(DateOnly fromDate, DateOnly toDate) =>
        Enumerable.Range(0, toDate.DayNumber - fromDate.DayNumber + 1).Select(fromDate.AddDays).ToHashSet();
}
