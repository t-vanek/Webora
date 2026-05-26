using Microsoft.EntityFrameworkCore;
using Webora.Application.Parking;
using Webora.Domain.Parking.Incentives;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Parking;

public sealed class IncentiveService(IDbContextFactory<WeboraDbContext> dbContextFactory) : IIncentiveService
{
    public async Task<ParkerScoreDto> GetScoreAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var score = await dbContext.ParkerScores.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

        var badges = await dbContext.UserBadges.AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.AwardedAtUtc)
            .Select(b => b.Badge)
            .ToListAsync(cancellationToken);

        return score is null
            ? new ParkerScoreDto(userId, 0, 0, 0, 0, 0, 0, badges)
            : new ParkerScoreDto(userId, score.Points, score.Credits, score.ReservationsCompleted,
                score.ReservationsReleased, score.OffPeakReservations, score.NoShows, badges);
    }

    public async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var top = await dbContext.ParkerScores.AsNoTracking()
            .OrderByDescending(s => s.Points)
            .ThenBy(s => s.UpdatedAtUtc)
            .Take(take)
            .Join(dbContext.Users, s => s.UserId, u => u.Id,
                (s, u) => new { s.UserId, s.Points, s.ReservationsReleased, s.OffPeakReservations, u.DisplayName, u.Email })
            .ToListAsync(cancellationToken);

        var ids = top.Select(t => t.UserId).ToList();
        var badgesByUser = (await dbContext.UserBadges.AsNoTracking()
                .Where(b => ids.Contains(b.UserId))
                .ToListAsync(cancellationToken))
            .GroupBy(b => b.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ParkingBadge>)g.Select(b => b.Badge).ToList());

        // Re-order in memory so the rank is correct regardless of how the join was translated.
        return top
            .OrderByDescending(t => t.Points)
            .ThenBy(t => t.DisplayName)
            .Select((t, index) => new LeaderboardEntryDto(
                index + 1,
                t.UserId,
                string.IsNullOrWhiteSpace(t.DisplayName) ? t.Email ?? string.Empty : t.DisplayName,
                t.Points,
                t.ReservationsReleased,
                t.OffPeakReservations,
                badgesByUser.GetValueOrDefault(t.UserId, []))).ToList();
    }

    public async Task<IReadOnlyList<PointsLedgerEntryDto>> GetHistoryAsync(Guid userId, int take = 50, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.PointsLedgerEntries.AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(take)
            .Select(e => new PointsLedgerEntryDto(e.Id, e.Reason, e.Points, e.ReservationId, e.OccurredAtUtc, e.Detail))
            .ToListAsync(cancellationToken);
    }
}
