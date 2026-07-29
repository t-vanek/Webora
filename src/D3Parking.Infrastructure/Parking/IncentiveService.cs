using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

public sealed class IncentiveService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IParkingSettingsService parkingSettings) : IIncentiveService
{
    public async Task<ParkerScoreDto> GetScoreAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var score = await dbContext.ParkerScores.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);

        var badges = await dbContext.UserBadges.AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.AwardedAtUtc)
            .Select(b => b.Badge)
            .ToListAsync(cancellationToken);

        return score is null
            ? new ParkerScoreDto(userId, 0, 0, 0, 0, 0, 0, 0, policy.TierFor(0), 0, badges)
            : new ParkerScoreDto(userId, score.Points, score.Credits, score.ReservationsCompleted,
                score.ReservationsReleased, score.OffPeakReservations, score.NoShows,
                score.CompletionStreak, policy.TierFor(score.Points), score.TrustScore, badges);
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

    public async Task<IReadOnlyList<TeamLeaderboardEntryDto>> GetTeamLeaderboardAsync(int take = 20, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Left-join users to their score so members without activity still count (as zero).
        var members = await (
            from u in dbContext.Users.AsNoTracking()
            where u.Department != null && u.Department != ""
            join s in dbContext.ParkerScores on u.Id equals s.UserId into ss
            from s in ss.DefaultIfEmpty()
            select new { u.Department, Points = s != null ? s.Points : 0, OffPeak = s != null ? s.OffPeakReservations : 0 })
            .ToListAsync(cancellationToken);

        return members
            // Case-insensitive like the SQL side: GetPeerComparisonAsync compares departments
            // under the server's CI collation, so "IT" and "It" are one team there — splitting
            // them here would show different averages between the two views.
            .GroupBy(m => m.Department!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Department = g.Key,
                Count = g.Count(),
                AvgPoints = g.Average(m => m.Points),
                AvgOffPeak = g.Average(m => (double)m.OffPeak),
            })
            .OrderByDescending(t => t.AvgPoints)
            .ThenBy(t => t.Department)
            .Take(take)
            .Select((t, index) => new TeamLeaderboardEntryDto(
                index + 1, t.Department, t.Count,
                (int)Math.Round(t.AvgPoints, MidpointRounding.AwayFromZero),
                Math.Round(t.AvgOffPeak, 1)))
            .ToList();
    }

    public async Task<PeerComparisonDto?> GetPeerComparisonAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var department = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Department)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(department))
        {
            return null;
        }

        var members = await (
            from u in dbContext.Users.AsNoTracking()
            where u.Department == department
            join s in dbContext.ParkerScores on u.Id equals s.UserId into ss
            from s in ss.DefaultIfEmpty()
            select new { u.Id, Points = s != null ? s.Points : 0, OffPeak = s != null ? s.OffPeakReservations : 0 })
            .ToListAsync(cancellationToken);

        if (members.Count == 0)
        {
            return null;
        }

        var me = members.FirstOrDefault(m => m.Id == userId);
        var myPoints = me?.Points ?? 0;
        var myOffPeak = me?.OffPeak ?? 0;
        var rank = members.OrderByDescending(m => m.Points).ThenBy(m => m.Id).ToList().FindIndex(m => m.Id == userId) + 1;

        return new PeerComparisonDto(
            department,
            members.Count,
            rank,
            myPoints,
            (int)Math.Round(members.Average(m => m.Points), MidpointRounding.AwayFromZero),
            myOffPeak,
            Math.Round(members.Average(m => (double)m.OffPeak), 1));
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
