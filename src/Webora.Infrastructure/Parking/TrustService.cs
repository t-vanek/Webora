using Microsoft.EntityFrameworkCore;
using Webora.Application.Parking;
using Webora.Domain.Parking;
using Webora.Domain.Parking.Incentives;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Parking;

/// <summary>
/// Builds a trust graph from positive sharing interactions (a guest completing a reservation on a
/// resident's shared spot) and runs a weighted PageRank. Trust flows along real, completed
/// transactions — which cost credits and occupy a spot — so it is expensive to farm. The result is
/// normalised to 0–100 relative to the most-trusted member and stored on each <see cref="ParkerScore"/>.
/// </summary>
public sealed class TrustService(
    IDbContextFactory<WeboraDbContext> dbContextFactory,
    TimeProvider timeProvider) : ITrustService
{
    private const double Damping = 0.85;
    private const int Iterations = 20;

    public async Task<int> ComputeTrustAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await dbContext.ParkingSettings
            .FirstOrDefaultAsync(s => s.Id == ParkingSettings.SingletonId, cancellationToken);
        if (settings is null || !settings.TrustEnabled)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        if (settings.LastTrustComputeUtc is { } last && (now - last).TotalHours < settings.TrustIntervalHours)
        {
            return 0;
        }

        // A satisfactory transaction: a guest completed a reservation on a resident's shared spot.
        var transactions = await (
            from r in dbContext.Reservations.AsNoTracking()
            where r.Status == ReservationStatus.Completed
            join s in dbContext.ParkingSpots on r.SpotId equals s.Id
            where s.OwnerId != null && s.OwnerId != r.UserId
            select new { Owner = s.OwnerId!.Value, Guest = r.UserId })
            .ToListAsync(cancellationToken);

        // Mark the run regardless, so it doesn't recompute every sweep when there's nothing to do.
        settings.MarkTrustComputed(now);

        if (transactions.Count == 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return 0;
        }

        // Symmetric weighted adjacency over the positive-interaction graph.
        var weights = new Dictionary<Guid, Dictionary<Guid, double>>();
        void AddEdge(Guid from, Guid to)
        {
            if (!weights.TryGetValue(from, out var row))
            {
                row = new Dictionary<Guid, double>();
                weights[from] = row;
            }

            row[to] = row.GetValueOrDefault(to) + 1.0;
        }

        foreach (var t in transactions)
        {
            AddEdge(t.Owner, t.Guest);
            AddEdge(t.Guest, t.Owner);
        }

        // Anti-collusion: cap how much any single counterpart contributes, so a tight reciprocal ring
        // can't pump each other's trust beyond a bound.
        var maxPairWeight = settings.MaxPairTrustWeight;
        foreach (var row in weights.Values)
        {
            foreach (var key in row.Keys.ToList())
            {
                if (row[key] > maxPairWeight)
                {
                    row[key] = maxPairWeight;
                }
            }
        }

        var nodes = weights.Keys.ToList();
        var n = nodes.Count;
        var outWeight = nodes.ToDictionary(u => u, u => weights[u].Values.Sum());
        var rank = nodes.ToDictionary(u => u, _ => 1.0 / n);

        // Weighted PageRank with teleport (damping) — the iconic trust/centrality iteration.
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var next = nodes.ToDictionary(u => u, _ => (1.0 - Damping) / n);
            foreach (var i in nodes)
            {
                var share = Damping * rank[i] / outWeight[i];
                foreach (var (j, w) in weights[i])
                {
                    next[j] += share * w;
                }
            }

            rank = next;
        }

        var maxRank = rank.Values.Max();
        var threshold = settings.TrustedBadgeThreshold;

        var scores = (await dbContext.ParkerScores
                .Where(s => nodes.Contains(s.UserId))
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.UserId);

        var alreadyTrusted = (await dbContext.UserBadges
                .Where(b => b.Badge == ParkingBadge.Trusted && nodes.Contains(b.UserId))
                .Select(b => b.UserId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var node in nodes)
        {
            var trust = maxRank > 0
                ? (int)Math.Round(100.0 * rank[node] / maxRank, MidpointRounding.AwayFromZero)
                : 0;

            if (!scores.TryGetValue(node, out var score))
            {
                score = new ParkerScore(node);
                dbContext.ParkerScores.Add(score);
            }

            score.SetTrust(trust, now);

            if (trust >= threshold && !alreadyTrusted.Contains(node))
            {
                dbContext.UserBadges.Add(new UserBadge(node, ParkingBadge.Trusted, now));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return nodes.Count;
    }
}
