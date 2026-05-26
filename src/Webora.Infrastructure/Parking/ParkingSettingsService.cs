using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Webora.Application.Parking;
using Webora.Domain.Accounts;
using Webora.Domain.Parking;
using Webora.Domain.Parking.Incentives;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Parking;

public sealed class ParkingSettingsService(
    IDbContextFactory<WeboraDbContext> dbContextFactory,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<ParkingSettingsService> logger) : IParkingSettingsService
{
    private const string PolicyCacheKey = "webora:parking-policy";

    public async Task<IncentivePolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(PolicyCacheKey, out var cached) && cached is IncentivePolicy policy)
        {
            return policy;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        policy = (await GetOrCreateAsync(dbContext, cancellationToken)).ToPolicy();

        using var entry = cache.CreateEntry(PolicyCacheKey);
        entry.Value = policy;
        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
        return policy;
    }

    public async Task<TimeSpan> GetSweepIntervalAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return (await GetOrCreateAsync(dbContext, cancellationToken)).SweepInterval;
    }

    public async Task<GeoPoint?> GetLotLocationAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var s = await GetOrCreateAsync(dbContext, cancellationToken);
        return s is { LotLatitude: { } lat, LotLongitude: { } lon } ? new GeoPoint(lat, lon) : null;
    }

    public async Task<ParkingSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var s = await GetOrCreateAsync(dbContext, cancellationToken);
        return new ParkingSettingsDto(
            s.ReleasePoints, s.OffPeakBonusPoints, s.NoShowPenaltyPoints,
            s.ReleaseCutoff, s.NoShowGracePeriod, s.ReminderLeadTime,
            s.PeakStart, s.PeakEnd, s.SweepInterval,
            s.ResidentHoldUntil, s.ResidentReleasePointsPerHour, s.ResidentReleaseMaxPoints,
            s.ResidentMaxShareAllowance, s.ResidentSharePercentPerAllowance, s.ResidentWastedShareClawbackPercent,
            s.LotLatitude, s.LotLongitude, s.SharedTakenBasePoints, s.SharedTakenReferenceKm, s.SharedTakenMaxMultiplier);
    }

    public async Task<ParkingResult> UpdateAsync(ParkingSettingsDto dto, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        if (dto.PeakEnd <= dto.PeakStart)
        {
            return ParkingResult.Failure("Parking_Settings_Error_PeakRange");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await GetOrCreateAsync(dbContext, cancellationToken);
        settings.Update(
            dto.ReleasePoints, dto.OffPeakBonusPoints, dto.NoShowPenaltyPoints,
            dto.ReleaseCutoff, dto.NoShowGracePeriod, dto.ReminderLeadTime,
            dto.PeakStart, dto.PeakEnd, dto.SweepInterval,
            dto.ResidentHoldUntil, dto.ResidentReleasePointsPerHour, dto.ResidentReleaseMaxPoints,
            dto.ResidentMaxShareAllowance, dto.ResidentSharePercentPerAllowance, dto.ResidentWastedShareClawbackPercent,
            dto.LotLatitude, dto.LotLongitude, dto.SharedTakenBasePoints, dto.SharedTakenReferenceKm, dto.SharedTakenMaxMultiplier);

        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actingUserId, AccountAuditEventType.SettingsChanged, $"admin:{actingUserId}",
            $"Parking: release={settings.ReleasePoints} offPeak={settings.OffPeakBonusPoints} noShow={settings.NoShowPenaltyPoints} " +
            $"peak={settings.PeakStart:HH\\:mm}-{settings.PeakEnd:HH\\:mm} sweep={settings.SweepInterval}",
            timeProvider.GetUtcNow()));

        await dbContext.SaveChangesAsync(cancellationToken);
        cache.Remove(PolicyCacheKey);

        logger.LogInformation("Parking settings changed by {AdminId}.", actingUserId);
        return ParkingResult.Success;
    }

    private static async Task<ParkingSettings> GetOrCreateAsync(WeboraDbContext dbContext, CancellationToken cancellationToken)
    {
        var settings = await dbContext.ParkingSettings
            .FirstOrDefaultAsync(s => s.Id == ParkingSettings.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        settings = ParkingSettings.CreateDefault();
        dbContext.ParkingSettings.Add(settings);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return settings;
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(settings).State = EntityState.Detached;
            return await dbContext.ParkingSettings.FirstAsync(s => s.Id == ParkingSettings.SingletonId, cancellationToken);
        }
    }
}
