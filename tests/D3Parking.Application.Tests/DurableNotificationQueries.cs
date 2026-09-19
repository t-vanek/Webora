using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace D3Parking.Application.Tests;

internal static class DurableNotificationQueries
{
    public static async Task<List<(Guid UserId, string Title)>> InboxAsync(DbContextOptions<D3ParkingDbContext> options)
    {
        await using var db = new D3ParkingDbContext(options);
        return (await db.Notifications.AsNoTracking().Select(n => new { n.UserId, n.Title }).ToListAsync())
            .Select(n => (n.UserId, n.Title)).ToList();
    }

    public static async Task<List<(Guid UserId, string Title)>> EmailsAsync(DbContextOptions<D3ParkingDbContext> options)
    {
        await using var db = new D3ParkingDbContext(options);
        return (await db.NotificationEmailDeliveries.AsNoTracking().Select(n => new { n.UserId, n.Title }).ToListAsync())
            .Select(n => (n.UserId, n.Title)).ToList();
    }
}
