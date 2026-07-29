using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Common;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

public sealed class ParkingSpotService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    INotificationService notifications,
    ISiteSettingsService siteSettings,
    TimeProvider timeProvider,
    IStringLocalizer<ParkingMessages> messages) : IParkingSpotService
{
    public async Task<IReadOnlyList<ParkingSpotDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.ParkingSpots.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(s => s.IsActive);
        }

        // Natural code order (D3-2 before D3-10) needs the comparer, so sort in memory —
        // the whole lot fits in one page anyway.
        var spots = await query
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId,
                s.OwnerId == null
                    ? null
                    : dbContext.Users.Where(u => u.Id == s.OwnerId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
                s.MonthlyShareAllowance))
            .ToListAsync(cancellationToken);
        return spots.OrderBy(s => s.Code, SpotCodeComparer.Instance).ToList();
    }

    public async Task<ParkingSpotDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId,
                s.OwnerId == null
                    ? null
                    : dbContext.Users.Where(u => u.Id == s.OwnerId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
                s.MonthlyShareAllowance))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ParkingResult> CreateAsync(string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return ParkingResult.Failure("Parking_Error_CodeRequired");
        }

        code = code.Trim();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await dbContext.ParkingSpots.AnyAsync(s => s.Code.ToLower() == code.ToLower(), cancellationToken))
        {
            return ParkingResult.Failure("Parking_Error_DuplicateCode");
        }

        dbContext.ParkingSpots.Add(new ParkingSpot(code, type, notes));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent create landed the same code between the check and the save; the unique
            // index on Code is the last line of defence. Safe to just retry.
            return ParkingResult.Failure("Parking_Error_DuplicateCode");
        }

        return ParkingResult.Success;
    }

    // Mirrors the ParkingSpots.Code column config in D3ParkingDbContext.
    private const int MaxCodeLength = 32;

    public async Task<SpotBatchPlan> PreviewBatchAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        var (valid, invalid) = NormalizeBatch(codes);
        if (valid.Count == 0)
        {
            return new SpotBatchPlan([], [], invalid);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ExistingCodesAsync(dbContext, valid, cancellationToken);
        return new SpotBatchPlan(
            valid.Where(c => !existing.Contains(c)).ToList(),
            valid.Where(existing.Contains).ToList(),
            invalid);
    }

    public async Task<SpotBatchResult> CreateBatchAsync(IReadOnlyList<string> codes, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default)
    {
        var (valid, invalid) = NormalizeBatch(codes);
        if (invalid.Count > 0)
        {
            // Refuse rather than silently drop: the admin sees the offending codes in the preview.
            return SpotBatchResult.Failure("Parking_Error_SeriesCodeTooLong");
        }

        if (valid.Count == 0)
        {
            return SpotBatchResult.Failure("Parking_Error_CodeRequired");
        }

        if (valid.Count > SpotCodeSeries.MaxBatchSize)
        {
            return SpotBatchResult.Failure("Parking_Error_SeriesTooLarge");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ExistingCodesAsync(dbContext, valid, cancellationToken);
        var toCreate = valid.Where(c => !existing.Contains(c)).ToList();
        var skipped = valid.Where(existing.Contains).ToList();

        if (toCreate.Count == 0)
        {
            return SpotBatchResult.Failure("Parking_Error_SeriesAllExist");
        }

        dbContext.ParkingSpots.AddRange(toCreate.Select(c => new ParkingSpot(c, type, notes)));
        try
        {
            // One SaveChanges = one transaction: the batch lands whole or not at all.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Somebody created a colliding code between the duplicate check and the save;
            // the unique index on Code is the last line of defence. Safe to just retry.
            return SpotBatchResult.Failure("Parking_Error_SeriesConflict");
        }

        return SpotBatchResult.Success(toCreate.Count, skipped);
    }

    /// <summary>Trims, drops empties, de-duplicates case-insensitively and splits off over-long codes.</summary>
    private static (List<string> Valid, List<string> Invalid) NormalizeBatch(IReadOnlyList<string> codes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var raw in codes)
        {
            var code = raw?.Trim();
            if (string.IsNullOrEmpty(code) || !seen.Add(code))
            {
                continue;
            }

            (code.Length > MaxCodeLength ? invalid : valid).Add(code);
        }

        return (valid, invalid);
    }

    private static async Task<HashSet<string>> ExistingCodesAsync(D3ParkingDbContext dbContext, List<string> codes, CancellationToken cancellationToken)
    {
        // Same case-insensitive comparison as the single-spot duplicate check above.
        var lowered = codes.Select(c => c.ToLower()).ToList();
        var existing = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => lowered.Contains(s.Code.ToLower()))
            .Select(s => s.Code)
            .ToListAsync(cancellationToken);
        return new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ParkingResult> UpdateAsync(Guid id, string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return ParkingResult.Failure("Parking_Error_CodeRequired");
        }

        code = code.Trim();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        if (await dbContext.ParkingSpots.AnyAsync(s => s.Id != id && s.Code.ToLower() == code.ToLower(), cancellationToken))
        {
            return ParkingResult.Failure("Parking_Error_DuplicateCode");
        }

        // The employee pool and the visitor agenda are separate booking systems that never see
        // each other's conflicts. Crossing the boundary with live bookings would double-let the
        // spot: a visitor spot with a future visit re-typed to Standard immediately reappears in
        // the employee pool, whose conflict check only reads Reservations (and vice versa).
        if (type != spot.Type && (type == ParkingSpotType.Visitor) != (spot.Type == ParkingSpotType.Visitor))
        {
            var now = timeProvider.GetUtcNow();
            var hasLiveBookings = spot.Type == ParkingSpotType.Visitor
                ? await dbContext.VisitorBookings.AnyAsync(b => b.SpotId == id
                    && b.Status == VisitorBookingStatus.Booked && b.EndUtc > now, cancellationToken)
                : await dbContext.Reservations.AnyAsync(r => r.SpotId == id
                    && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                    && r.EndUtc > now, cancellationToken);
            if (hasLiveBookings)
            {
                return ParkingResult.Failure("Parking_Error_TypeChangeBooked");
            }

            // Visitor spots never belong to a resident (AssignOwnerAsync refuses them too).
            if (type == ParkingSpotType.Visitor && spot.OwnerId is not null)
            {
                return ParkingResult.Failure("Parking_Error_VisitorSpotNoOwner");
            }
        }

        spot.Rename(code);
        spot.ChangeType(type);
        spot.UpdateNotes(notes);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent rename landed the same code between the check and the save; the unique
            // index on Code is the last line of defence. Safe to just retry.
            return ParkingResult.Failure("Parking_Error_DuplicateCode");
        }

        return ParkingResult.Success;
    }

    public async Task<ParkingResult> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        if (active)
        {
            spot.Activate();
        }
        else
        {
            spot.Deactivate();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Deactivating a spot leaves its upcoming reservations stranded, so warn the holders to re-book.
        if (!active)
        {
            var now = timeProvider.GetUtcNow();
            var affected = await dbContext.Reservations.AsNoTracking()
                .Where(r => r.SpotId == id && r.EndUtc >= now
                    && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn))
                .Select(r => r.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var holderId in affected)
            {
                await notifications.NotifyAsync(holderId, NotificationCategory.Administrative, NotificationLevel.Warning,
                    messages["Parking_Notify_SpotDeactivated_Title"],
                    messages["Parking_Notify_SpotDeactivated_Body", spot.Code], email: true, cancellationToken);
            }
        }

        return ParkingResult.Success;
    }

    public async Task<ParkingResult> AssignOwnerAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        if (ownerId is { } owner && !await dbContext.Users.AnyAsync(u => u.Id == owner, cancellationToken))
        {
            return ParkingResult.Failure("Parking_Error_UserNotFound");
        }

        // Visitor spots belong to the reception's agenda; splicing a resident's flows into it
        // would cross two booking systems that never see each other's conflicts.
        if (ownerId is not null && spot.Type == ParkingSpotType.Visitor)
        {
            return ParkingResult.Failure("Parking_Error_VisitorSpotNoOwner");
        }

        // One spot per resident: every resident flow resolves the user's spot with an unordered
        // FirstOrDefault, so a second assignment would make confirm/release/allowance act on a
        // nondeterministic one of the two. The filtered unique index on OwnerId backs this.
        if (ownerId is { } candidate
            && await dbContext.ParkingSpots.AnyAsync(s => s.Id != id && s.OwnerId == candidate, cancellationToken))
        {
            return ParkingResult.Failure("Parking_Error_OwnerHasSpot");
        }

        var previousOwner = spot.OwnerId;
        spot.AssignOwner(ownerId);

        if (previousOwner != ownerId)
        {
            // Ownership changed: the previous owner's shares no longer speak for this spot. Left
            // in place they would keep the new resident's spot publicly bookable and route
            // wasted-share clawbacks to the old owner. Future days are removed and their prepaid
            // rewards revoked; past days keep their history — those were genuinely shared.
            var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
            var today = SiteTime.Today(timeProvider.GetUtcNow(), timeZone);
            var futureReleases = await dbContext.SpotReleases
                .Where(r => r.SpotId == spot.Id && r.Date >= today)
                .ToListAsync(cancellationToken);

            if (futureReleases.Count > 0)
            {
                var now = timeProvider.GetUtcNow();
                foreach (var ownerRevocations in futureReleases
                    .Where(r => r.AwardedPoints > 0)
                    .GroupBy(r => r.OwnerId))
                {
                    // Whatever the no-show sweep already clawed back from a day's award must not
                    // be revoked again — ClawedBackPoints exists precisely to cap the total
                    // penalty at the award, never taking the owner net negative.
                    var revoked = ownerRevocations.Sum(r => r.AwardedPoints - r.ClawedBackPoints);
                    var ownerScore = await dbContext.ParkerScores
                        .FirstOrDefaultAsync(s => s.UserId == ownerRevocations.Key, cancellationToken);
                    if (ownerScore is not null && revoked > 0)
                    {
                        ownerScore.RevokeSharePoints(revoked, now);
                        dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                            ownerRevocations.Key, IncentiveReason.ResidentShareUnused, -revoked, null, now, spot.Code));
                    }
                }

                dbContext.SpotReleases.RemoveRange(futureReleases);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Tell the affected residents: a removed/replaced owner loses the spot, a new owner gains one.
        if (previousOwner != ownerId)
        {
            // Both directions mirror to email: gaining or losing a resident spot is a formal
            // change of what the person is entitled to, worth a durable record.
            if (previousOwner is { } prev)
            {
                await notifications.NotifyAsync(prev, NotificationCategory.Administrative, NotificationLevel.Info,
                    messages["Parking_Notify_ResidentUnassigned_Title"],
                    messages["Parking_Notify_ResidentUnassigned_Body", spot.Code], email: true, cancellationToken);
            }

            if (ownerId is { } newOwner)
            {
                await notifications.NotifyAsync(newOwner, NotificationCategory.Administrative, NotificationLevel.Info,
                    messages["Parking_Notify_ResidentAssigned_Title"],
                    messages["Parking_Notify_ResidentAssigned_Body", spot.Code], email: true, cancellationToken);
            }
        }

        return ParkingResult.Success;
    }

    public async Task<IReadOnlyList<OccupancyMismatchDto>> GetOccupancyMismatchesAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(take, 1, 500);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Reporter and replacement spot are left-joined: a deleted account or spot must not hide
        // the trend row.
        var mismatches = await (from m in dbContext.OccupancyMismatches.AsNoTracking()
                                join s in dbContext.ParkingSpots.AsNoTracking() on m.SpotId equals s.Id
                                join u in dbContext.Users.AsNoTracking() on m.ReporterId equals u.Id into reporters
                                from reporter in reporters.DefaultIfEmpty()
                                join rs in dbContext.ParkingSpots.AsNoTracking() on m.RelocatedToSpotId equals rs.Id into replacements
                                from replacement in replacements.DefaultIfEmpty()
                                orderby m.ReportedAtUtc descending
                                select new
                                {
                                    m.Id,
                                    m.SpotId,
                                    m.ReservationId,
                                    s.Code,
                                    m.StartUtc,
                                    m.EndUtc,
                                    m.ReportedAtUtc,
                                    m.BlockerPlate,
                                    ReporterName = reporter != null ? (reporter.DisplayName ?? reporter.Email ?? string.Empty) : string.Empty,
                                    ReporterEmail = reporter != null ? reporter.Email : null,
                                    ReplacementCode = replacement != null ? replacement.Code : null,
                                })
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (mismatches.Count == 0)
        {
            return [];
        }

        // Recorded plates are matched against registered ones with spacing/dash/case ignored —
        // a plate is copied off a car in a hurry, "3AB 1234" and "3ab-1234" must still meet.
        // Employee plates first; visitor bookings answer the plates employees cannot.
        var plateOwners = new Dictionary<string, (string Name, string? Email)>();
        var visitorPlates = new List<(string Normalized, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Label)>();
        if (mismatches.Any(m => m.BlockerPlate is not null))
        {
            var registered = await dbContext.Users.AsNoTracking()
                .Where(u => u.LicensePlate != null)
                .Select(u => new { u.LicensePlate, u.DisplayName, u.Email })
                .ToListAsync(cancellationToken);
            foreach (var owner in registered)
            {
                plateOwners[NormalizePlate(owner.LicensePlate!)] = (owner.DisplayName ?? owner.Email ?? string.Empty, owner.Email);
            }

            // Fleet plates answer as a fallback (TryAdd): a driver's own profile plate names the
            // person directly, which beats the car — but an unpaired or manually paired company
            // car would otherwise read as "unknown plate".
            var fleetVehicles = await (from v in dbContext.CompanyVehicles.AsNoTracking()
                                       join u in dbContext.Users.AsNoTracking() on v.PairedUserId equals u.Id into drivers
                                       from driver in drivers.DefaultIfEmpty()
                                       select new
                                       {
                                           v.NormalizedPlate,
                                           v.Plate,
                                           v.Name,
                                           DriverName = driver != null ? (driver.DisplayName ?? driver.Email) : null,
                                           DriverEmail = driver != null ? driver.Email : null,
                                       })
                .ToListAsync(cancellationToken);
            foreach (var vehicle in fleetVehicles)
            {
                var vehicleLabel = messages["Fleet_Mismatch_Vehicle",
                    vehicle.Name is null ? vehicle.Plate : $"{vehicle.Name} ({vehicle.Plate})"].Value;
                plateOwners.TryAdd(vehicle.NormalizedPlate, (
                    vehicle.DriverName is null ? vehicleLabel : $"{vehicleLabel} — {vehicle.DriverName}",
                    vehicle.DriverEmail));
            }

            var mismatchMinStart = mismatches.Min(m => m.StartUtc);
            var mismatchMaxEnd = mismatches.Max(m => m.EndUtc);
            visitorPlates = (await dbContext.VisitorBookings.AsNoTracking()
                    .Where(b => b.LicensePlate != null && b.Status == VisitorBookingStatus.Booked
                        && b.StartUtc < mismatchMaxEnd && b.EndUtc > mismatchMinStart)
                    .Select(b => new { b.LicensePlate, b.StartUtc, b.EndUtc, b.VisitorName, b.Company })
                    .ToListAsync(cancellationToken))
                .Select(b => (NormalizePlate(b.LicensePlate!), b.StartUtc, b.EndUtc,
                    b.Company is null ? b.VisitorName : $"{b.VisitorName} ({b.Company})"))
                .ToList();
        }

        // The admin's shortlist for "who blocked the spot": every reservation that met the
        // mismatch window on the same spot (typically a no-show whose car may still be there, or
        // a checked-in holder who overstayed). Fetched in one sweep over the covered range and
        // paired in memory — the view is capped at `limit` rows, so this stays small.
        var spotIds = mismatches.Select(x => x.SpotId).Distinct().ToList();
        var minStart = mismatches.Min(x => x.StartUtc);
        var maxEnd = mismatches.Max(x => x.EndUtc);
        var candidates = await (from r in dbContext.Reservations.AsNoTracking()
                                join u in dbContext.Users.AsNoTracking() on r.UserId equals u.Id into holders
                                from holder in holders.DefaultIfEmpty()
                                where spotIds.Contains(r.SpotId)
                                    && r.StartUtc < maxEnd && r.EndUtc > minStart
                                    && (r.Status == ReservationStatus.NoShow
                                        || r.Status == ReservationStatus.CheckedIn
                                        || r.Status == ReservationStatus.Completed
                                        || r.Status == ReservationStatus.Reserved)
                                select new
                                {
                                    r.Id,
                                    r.SpotId,
                                    r.StartUtc,
                                    r.EndUtc,
                                    r.Status,
                                    Name = holder != null ? (holder.DisplayName ?? holder.Email ?? string.Empty) : string.Empty,
                                    Email = holder != null ? holder.Email : null,
                                })
            .ToListAsync(cancellationToken);

        return mismatches
            .Select(m =>
            {
                (string Name, string? Email)? match = null;
                var matchIsVisitor = false;
                if (m.BlockerPlate is not null)
                {
                    var normalized = NormalizePlate(m.BlockerPlate);
                    if (plateOwners.TryGetValue(normalized, out var owner))
                    {
                        match = owner;
                    }
                    else
                    {
                        var visitor = visitorPlates.FirstOrDefault(v => v.Normalized == normalized
                            && v.StartUtc < m.EndUtc && v.EndUtc > m.StartUtc);
                        if (visitor.Label is not null)
                        {
                            match = (visitor.Label, null);
                            matchIsVisitor = true;
                        }
                    }
                }

                return new OccupancyMismatchDto(
                m.Id,
                m.Code,
                m.StartUtc,
                m.EndUtc,
                m.ReportedAtUtc,
                m.ReporterName,
                m.ReporterEmail,
                m.ReplacementCode,
                m.BlockerPlate,
                match?.Name,
                match?.Email,
                matchIsVisitor,
                candidates
                    .Where(c => c.SpotId == m.SpotId && c.Id != m.ReservationId
                        && c.StartUtc < m.EndUtc && c.EndUtc > m.StartUtc)
                    // No-shows first — that is almost always the answer the admin is looking for.
                    .OrderBy(c => c.Status switch
                    {
                        ReservationStatus.NoShow => 0,
                        ReservationStatus.CheckedIn => 1,
                        _ => 2,
                    })
                    .Select(c => new MismatchRelatedReservationDto(c.Name, c.Email, c.Status))
                    .ToList());
            })
            .ToList();
    }

    /// <summary>Spacing-, dash- and case-insensitive plate form used for matching.</summary>
    private static string NormalizePlate(string plate) => PlateNormalizer.Normalize(plate);
}
