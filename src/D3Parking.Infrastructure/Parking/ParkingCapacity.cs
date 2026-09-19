using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace D3Parking.Infrastructure.Parking;

/// <summary>Physical employee-pool availability shared by search, quotes, incidents and the queue.</summary>
internal static class ParkingCapacity
{
    public static async Task<List<ParkingSpot>> AvailableAsync(D3ParkingDbContext db,
        DateTimeOffset start, DateTimeOffset end, DateTimeOffset now, TimeZoneInfo zone,
        bool includeOffers, CancellationToken ct)
    {
        var first = SiteTime.Today(start, zone);
        var last = SiteTime.Today(end.AddTicks(-1), zone);
        var days = last.DayNumber - first.DayNumber + 1;
        var released = db.SpotReleases.Where(r => r.Date >= first && r.Date <= last)
            .GroupBy(r => r.SpotId).Where(g => g.Count() == days).Select(g => g.Key);
        var residentSpots = db.ParkingSpotResidents.Where(r => r.RemovedAtUtc == null).Select(r => r.SpotId);
        var booked = db.Reservations.Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < end && r.EndUtc > start).Select(r => r.SpotId);
        var blocked = db.OccupancyMismatches.Where(m => m.ResolvedAtUtc == null && m.ReportedAtUtc < end
            && m.EndUtc > start && m.EndUtc > now).Select(m => m.SpotId);
        var held = db.QueueEntries.Where(q => q.Status == QueueEntryStatus.Offered && q.OfferExpiresAtUtc > now
            && q.StartUtc < end && q.EndUtc > start && q.OfferedSpotId != null).Select(q => q.OfferedSpotId!.Value);
        return await db.ParkingSpots.AsNoTracking().Where(s => s.IsActive && s.Type != ParkingSpotType.Visitor
            && !booked.Contains(s.Id) && !blocked.Contains(s.Id) && (!includeOffers || !held.Contains(s.Id))
            && ((s.OwnerId == null && !residentSpots.Contains(s.Id)) || released.Contains(s.Id)))
            .OrderBy(s => s.Code).ToListAsync(ct);
    }

    public static Task<bool> IsBlockedAsync(D3ParkingDbContext db, Guid spotId, DateTimeOffset start,
        DateTimeOffset end, DateTimeOffset now, CancellationToken ct) =>
        db.OccupancyMismatches.AnyAsync(m => m.SpotId == spotId && m.ResolvedAtUtc == null
            && m.ReportedAtUtc < end && m.EndUtc > start && m.EndUtc > now, ct);
}
