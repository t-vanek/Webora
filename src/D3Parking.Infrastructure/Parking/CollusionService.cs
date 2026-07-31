using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Oversight;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

/// <summary>
/// Flags reciprocal sharing rings: pairs whose shared-spot transactions are heavily concentrated on
/// each other. Detection only — every flag becomes an oversight case, and that is where it is ruled on.
/// </summary>
public sealed class CollusionService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    TimeProvider timeProvider,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages) : ICollusionService
{
    public async Task<int> ScanAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await dbContext.ParkingSettings
            .FirstOrDefaultAsync(s => s.Id == ParkingSettings.SingletonId, cancellationToken);
        if (settings is null || !settings.AntiCollusionEnabled)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        if (settings.LastCollusionScanUtc is { } last && (now - last).TotalHours < settings.CollusionScanIntervalHours)
        {
            return 0;
        }

        var transactions = await (
            from r in dbContext.Reservations.AsNoTracking()
            where r.Status == ReservationStatus.Completed
            join s in dbContext.ParkingSpots on r.SpotId equals s.Id
            where s.OwnerId != null && s.OwnerId != r.UserId
            select new { Owner = s.OwnerId!.Value, Guest = r.UserId })
            .ToListAsync(cancellationToken);

        settings.MarkCollusionScanned(now);

        if (transactions.Count == 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return 0;
        }

        var total = new Dictionary<Guid, int>();
        var mutual = new Dictionary<(Guid, Guid), int>();
        foreach (var t in transactions)
        {
            total[t.Owner] = total.GetValueOrDefault(t.Owner) + 1;
            total[t.Guest] = total.GetValueOrDefault(t.Guest) + 1;
            var key = CollusionFlag.Key(t.Owner, t.Guest);
            mutual[key] = mutual.GetValueOrDefault(key) + 1;
        }

        var existing = (await dbContext.CollusionFlags.ToListAsync(cancellationToken))
            .ToDictionary(f => (f.UserA, f.UserB));

        // Pairs a reviewer has already dismissed. The verdict lives on the oversight case, so this
        // is where the scan reads it: re-measuring a pair that was ruled a false positive would put
        // it back on the desk every night with the same numbers and the same answer.
        var dismissed = await dbContext.OversightCases.AsNoTracking()
            .Where(c => c.Kind == OversightCaseKind.CollusionRing && c.Resolution == OversightResolution.Unfounded)
            .Select(c => c.SubjectId)
            .ToListAsync(cancellationToken);
        var dismissedFlags = dismissed.ToHashSet();

        var newPairs = new List<(Guid A, Guid B)>();
        foreach (var ((a, b), m) in mutual)
        {
            if (m < settings.CollusionMinInteractions)
            {
                continue;
            }

            var concA = m * 100 / total[a];
            var concB = m * 100 / total[b];
            if (concA < settings.CollusionConcentrationPercent || concB < settings.CollusionConcentrationPercent)
            {
                continue;
            }

            if (existing.TryGetValue((a, b), out var flag))
            {
                if (dismissedFlags.Contains(flag.Id))
                {
                    continue;
                }

                flag.Update(m, concA, concB, now);
            }
            else
            {
                dbContext.CollusionFlags.Add(new CollusionFlag(a, b, m, concA, concB, now));
                newPairs.Add((a, b));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (newPairs.Count > 0)
        {
            var adminRoleId = await dbContext.Roles
                .Where(r => r.Name == Roles.Administrator)
                .Select(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);
            var adminIds = await dbContext.UserRoles
                .Where(ur => ur.RoleId == adminRoleId)
                .Select(ur => ur.UserId)
                .ToListAsync(cancellationToken);

            foreach (var adminId in adminIds)
            {
                await notifications.NotifyAsync(adminId, NotificationCategory.Administrative, NotificationLevel.Warning,
                    messages["Parking_Notify_Collusion_Title"],
                    messages["Parking_Notify_Collusion_Body", newPairs.Count], cancellationToken);
            }
        }

        return newPairs.Count;
    }

    public async Task<CollusionFlagDto?> GetFlagAsync(Guid flagId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var flag = await dbContext.CollusionFlags.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == flagId, cancellationToken);
        if (flag is null)
        {
            return null;
        }

        var names = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == flag.UserA || u.Id == flag.UserB)
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email ?? string.Empty, cancellationToken);

        return new CollusionFlagDto(
            flag.Id,
            names.GetValueOrDefault(flag.UserA, string.Empty),
            names.GetValueOrDefault(flag.UserB, string.Empty),
            flag.MutualInteractions, flag.ConcentrationAPercent, flag.ConcentrationBPercent,
            flag.DetectedAtUtc, flag.UpdatedAtUtc);
    }
}
