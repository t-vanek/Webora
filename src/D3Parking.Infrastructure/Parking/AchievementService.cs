using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

/// <summary>
/// Private read model for positive achievements. It deliberately exposes no leaderboard, points,
/// tiers, trust score or peer comparison.
/// </summary>
public sealed class AchievementService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory) : IAchievementService
{
    public async Task<AchievementSummaryDto> GetSummaryAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var credits = await dbContext.ParkerScores.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => (int?)s.Credits)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        var achievements = (await dbContext.UserBadges.AsNoTracking()
                .Where(b => b.UserId == userId)
                .OrderBy(b => b.AwardedAtUtc)
                .Select(b => b.Badge)
                .ToListAsync(cancellationToken))
            .Where(ParkingAchievementRules.IsPositiveAchievement)
            .ToList();

        var plansCreated = await dbContext.Reservations.AsNoTracking()
            .CountAsync(r => r.UserId == userId, cancellationToken);
        var contributionCounts = await dbContext.ParkingContributions.AsNoTracking()
            .Where(c => c.UserId == userId)
            .GroupBy(c => c.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Kind, g => g.Count, cancellationToken);

        return new AchievementSummaryDto(
            userId,
            credits,
            achievements,
            plansCreated,
            contributionCounts.GetValueOrDefault(ParkingContributionKind.UsefulRelease),
            contributionCounts.GetValueOrDefault(ParkingContributionKind.QueueHelped),
            contributionCounts.GetValueOrDefault(ParkingContributionKind.ResidentShareUsed));
    }
}
