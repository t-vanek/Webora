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
        var residents = await dbContext.ParkingSpotResidents.AsNoTracking()
            .Where(r => r.SpotId == spot.Id && r.RemovedAtUtc == null)
            .OrderBy(r => r.AssignedAtUtc)
            .ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.UserId })
            .ToListAsync(cancellationToken);

        if (residents.Count == 0)
        {
            return spot.OwnerId is { } ownerId
                ? AllDates(fromDate, toDate).ToDictionary(date => date, _ => ownerId)
                : new Dictionary<DateOnly, Guid>();
        }

        if (residents.Count == 1)
        {
            return AllDates(fromDate, toDate).ToDictionary(date => date, _ => residents[0].UserId);
        }

        var residentById = residents.ToDictionary(r => r.Id, r => r.UserId);
        var explicitAssignments = await dbContext.SpotDayAssignments.AsNoTracking()
            .Where(a => a.SpotId == spot.Id && a.Date >= fromDate && a.Date <= toDate)
            .Select(a => new { a.Date, a.ResidentId })
            .ToListAsync(cancellationToken);
        var overrides = explicitAssignments
            .Where(a => residentById.ContainsKey(a.ResidentId))
            .ToDictionary(a => a.Date, a => residentById[a.ResidentId]);

        var assigned = new Dictionary<DateOnly, Guid>();
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            var assignedUser = overrides.GetValueOrDefault(date);
            if (assignedUser == Guid.Empty)
            {
                assignedUser = residents[Math.Abs(date.DayNumber % residents.Count)].UserId;
            }

            assigned[date] = assignedUser;
        }

        return assigned;
    }

    private static HashSet<DateOnly> AllDates(DateOnly fromDate, DateOnly toDate) =>
        Enumerable.Range(0, toDate.DayNumber - fromDate.DayNumber + 1).Select(fromDate.AddDays).ToHashSet();
}
