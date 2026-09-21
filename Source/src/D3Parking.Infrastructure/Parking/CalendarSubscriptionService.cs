using System.Security.Cryptography;
using System.Text;
using D3Parking.Application.Parking;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace D3Parking.Infrastructure.Parking;

public sealed class CalendarSubscriptionService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    TimeProvider timeProvider) : ICalendarSubscriptionService
{
    private const int TokenBytes = 32;
    private const int TokenLength = TokenBytes * 2;

    public async Task<CalendarSubscriptionStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await dbContext.CalendarSubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => new { s.CreatedAtUtc, s.UpdatedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        return item is null
            ? new CalendarSubscriptionStatus(false, null, null)
            : new CalendarSubscriptionStatus(true, item.CreatedAtUtc, item.UpdatedAtUtc);
    }

    public async Task<CalendarSubscriptionSecret> CreateOrRotateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user is required.", nameof(userId));

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenBytes)).ToLowerInvariant();
        var tokenHash = HashToken(token);
        var now = timeProvider.GetUtcNow();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var subscription = await dbContext.CalendarSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        if (subscription is null)
        {
            subscription = new CalendarSubscription(userId, tokenHash, now);
            dbContext.CalendarSubscriptions.Add(subscription);
        }
        else
        {
            subscription.Rotate(tokenHash, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new CalendarSubscriptionSecret(token,
            new CalendarSubscriptionStatus(true, subscription.CreatedAtUtc, subscription.UpdatedAtUtc));
    }

    public async Task DisableAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.CalendarSubscriptions.Where(s => s.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<Guid?> ResolveUserAsync(string token, CancellationToken cancellationToken = default)
    {
        // Reject malformed route values before hashing/querying. Besides reducing pointless database
        // traffic this keeps the credential format narrow and unambiguous.
        if (token.Length != TokenLength || token.Any(c => !char.IsAsciiHexDigit(c))) return null;

        var tokenHash = HashToken(token.ToLowerInvariant());
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.CalendarSubscriptions.AsNoTracking()
            .Where(s => s.TokenHash == tokenHash)
            .Select(s => (Guid?)s.UserId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReservationDto>> GetFeedReservationsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var oldestEnd = timeProvider.GetUtcNow().AddDays(-30);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await (from r in dbContext.Reservations.AsNoTracking()
                      join s in dbContext.ParkingSpots.AsNoTracking() on r.SpotId equals s.Id
                      where r.UserId == userId && r.EndUtc >= oldestEnd
                      orderby r.StartUtc
                      select new ReservationDto(
                          r.Id, r.SpotId, s.Code, s.Type, r.UserId,
                          r.StartUtc, r.EndUtc, r.Status, r.IsOffPeak, r.CreatedAtUtc,
                          r.CheckedInAtUtc, r.ReleasedAtUtc, r.CompletedAtUtc,
                          r.CalendarSequence, r.CalendarUpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
