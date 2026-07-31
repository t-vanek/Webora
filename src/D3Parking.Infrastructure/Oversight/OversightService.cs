using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Notifications;
using D3Parking.Application.Oversight;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Common;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Oversight;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Oversight;

/// <summary>
/// The oversight desk. Cases are opened by <see cref="EnsureCasesAsync"/> rather than by the
/// services that raise the signals: a report is recorded on the driver's critical path and the
/// collusion scan runs in the maintenance loop, and neither should fail — or hold the user up —
/// because the review queue was busy. Reconciling instead keeps the coupling one-way and makes the
/// same code the migration for every signal raised before cases existed.
/// </summary>
public sealed class OversightService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IParkingSpotService spots,
    ICollusionService collusion,
    ISiteSettingsService siteSettings,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages,
    TimeProvider timeProvider) : IOversightService
{
    /// <summary>How the two flagged names are joined into one line for the queue.</summary>
    private const string PairSeparator = " ↔ ";

    public Task<int> EnsureCasesAsync(CancellationToken cancellationToken = default) =>
        // A unique (Kind, SubjectId) violation means the queue's own load beat the sweep to it (or
        // the other way round); re-reading finds the case already there and opens nothing.
        OptimisticConcurrency.RetryAsync(() => IngestAsync(cancellationToken), cancellationToken);

    private async Task<int> IngestAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var mismatches = await dbContext.OccupancyMismatches.AsNoTracking()
            .Where(m => !dbContext.OversightCases.Any(c =>
                c.Kind == OversightCaseKind.OccupancyMismatch && c.SubjectId == m.Id))
            .Select(m => new { m.Id, m.SpotId, m.ReporterId, m.ReportedAtUtc, m.StartUtc, m.EndUtc, m.BlockerPlate })
            .OrderBy(m => m.ReportedAtUtc)
            .ToListAsync(cancellationToken);

        var flags = await dbContext.CollusionFlags.AsNoTracking()
            .Where(f => !dbContext.OversightCases.Any(c =>
                c.Kind == OversightCaseKind.CollusionRing && c.SubjectId == f.Id))
            .Select(f => new { f.Id, f.DetectedAtUtc, f.MutualInteractions, f.ConcentrationAPercent, f.ConcentrationBPercent })
            .OrderBy(f => f.DetectedAtUtc)
            .ToListAsync(cancellationToken);

        var defects = await dbContext.SpotDefectReports.AsNoTracking()
            .Where(d => !dbContext.OversightCases.Any(c =>
                c.Kind == OversightCaseKind.SpotDefect && c.SubjectId == d.Id))
            .Select(d => new { d.Id, d.SpotId, d.ReporterId, d.ReportedAtUtc })
            .OrderBy(d => d.ReportedAtUtc)
            .ToListAsync(cancellationToken);

        if (mismatches.Count == 0 && flags.Count == 0 && defects.Count == 0)
        {
            return 0;
        }

        var settings = await LoadSettingsAsync(dbContext, cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var announce = new List<(OversightCase Case, string Subject)>();

        foreach (var mismatch in mismatches)
        {
            // Opened when the signal was raised, not when the ingest noticed: a report that sat
            // through a weekend must read as two days old, or the queue's age column lies.
            var opened = new OversightCase(
                OversightCaseKind.OccupancyMismatch, mismatch.Id, mismatch.ReportedAtUtc,
                spotId: mismatch.SpotId, reporterId: mismatch.ReporterId);
            dbContext.OversightCases.Add(opened);
            dbContext.OversightCaseEvents.Add(
                OversightCaseEvent.FromSystem(opened.Id, OversightEventType.Opened, mismatch.ReportedAtUtc));

            // How often this spot has come up lately decides whether this is an incident or a
            // pattern. Cases added earlier in this same batch count: two reports arriving in one
            // sweep are still two reports.
            var since = mismatch.ReportedAtUtc.AddDays(-settings.OversightRecurrenceWindowDays);
            var repeats = await dbContext.OversightCases
                .CountAsync(c => c.SpotId == mismatch.SpotId && c.OpenedAtUtc >= since, cancellationToken)
                + dbContext.ChangeTracker.Entries<OversightCase>()
                    .Count(e => e.State == EntityState.Added && e.Entity != opened
                        && e.Entity.SpotId == mismatch.SpotId && e.Entity.OpenedAtUtc >= since)
                + 1;

            var fromRecurrence = repeats >= settings.OversightRecurrenceThreshold * 2 ? OversightCasePriority.Critical
                : repeats >= settings.OversightRecurrenceThreshold ? OversightCasePriority.High
                : OversightCasePriority.Normal;

            // A car nobody in the system owns, sitting on somebody's spot: nothing here can be
            // resolved by the incentive rules, only by a person going out there. Today's window
            // makes it critical — tomorrow it is a record, today it is a car in the way.
            var outsider = mismatch.BlockerPlate is { } plate
                && !await IsKnownPlateAsync(dbContext, plate, mismatch.StartUtc, mismatch.EndUtc, cancellationToken);
            var fromPlate = !outsider ? OversightCasePriority.Normal
                : SiteTime.Today(mismatch.StartUtc, timeZone) == SiteTime.Today(now, timeZone)
                    ? OversightCasePriority.Critical
                    : OversightCasePriority.High;

            var priority = (OversightCasePriority)Math.Max((int)fromRecurrence, (int)fromPlate);
            opened.OpenAt(priority, settings.SlaFor(priority));

            if (fromRecurrence != OversightCasePriority.Normal)
            {
                var reason = messages["Parking_Oversight_Reason_Recurrence", repeats, settings.OversightRecurrenceWindowDays];
                dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromSystem(
                    opened.Id, OversightEventType.Escalated, mismatch.ReportedAtUtc, reason));

                // Tell whoever is already holding an open case on this spot that it happened
                // again — the pattern is the point, and they would otherwise never see the rest of it.
                await AppendToOpenCasesOnSpotAsync(
                    dbContext, mismatch.SpotId, opened.Id, reason, mismatch.ReportedAtUtc, cancellationToken);
            }

            if (outsider)
            {
                dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromSystem(
                    opened.Id, OversightEventType.Escalated, mismatch.ReportedAtUtc,
                    messages["Parking_Oversight_Reason_OutsiderPlate", mismatch.BlockerPlate!]));
            }

            announce.Add((opened, await SpotCodeAsync(dbContext, mismatch.SpotId, cancellationToken)));
        }

        foreach (var flag in flags)
        {
            var opened = new OversightCase(OversightCaseKind.CollusionRing, flag.Id, flag.DetectedAtUtc);
            dbContext.OversightCases.Add(opened);
            dbContext.OversightCaseEvents.Add(
                OversightCaseEvent.FromSystem(opened.Id, OversightEventType.Opened, flag.DetectedAtUtc));

            // A pair only just over the detection threshold is worth a look; one trading almost
            // exclusively with itself is worth a look today.
            var blatant = Math.Min(flag.ConcentrationAPercent, flag.ConcentrationBPercent) >= 90
                && flag.MutualInteractions >= settings.CollusionMinInteractions * 2;
            var priority = blatant ? OversightCasePriority.High : OversightCasePriority.Normal;
            opened.OpenAt(priority, settings.SlaFor(priority));

            if (blatant)
            {
                dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromSystem(
                    opened.Id, OversightEventType.Escalated, flag.DetectedAtUtc,
                    messages["Parking_Oversight_Reason_Concentration",
                        Math.Min(flag.ConcentrationAPercent, flag.ConcentrationBPercent), flag.MutualInteractions]));
            }

            announce.Add((opened, PairLabel(flag.ConcentrationAPercent, flag.ConcentrationBPercent)));
        }

        foreach (var defect in defects)
        {
            // No triage rules here: nobody is accused, there is nothing to weigh, and a burnt-out
            // light is not more or less urgent for having been reported twice.
            var opened = new OversightCase(
                OversightCaseKind.SpotDefect, defect.Id, defect.ReportedAtUtc,
                spotId: defect.SpotId, reporterId: defect.ReporterId);
            dbContext.OversightCases.Add(opened);
            opened.OpenAt(OversightCasePriority.Normal, settings.SlaFor(OversightCasePriority.Normal));
            dbContext.OversightCaseEvents.Add(
                OversightCaseEvent.FromSystem(opened.Id, OversightEventType.Opened, defect.ReportedAtUtc));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Only the ones that will not wait for tomorrow's digest.
        foreach (var (@case, subject) in announce.Where(a => a.Case.Priority >= OversightCasePriority.High))
        {
            await NotifyReviewersAsync(dbContext, @case.Kind, NotificationLevel.Warning,
                messages["Parking_Notify_OversightNew_Title"],
                messages["Parking_Notify_OversightNew_Body", @case.Number, subject], cancellationToken);
        }

        return mismatches.Count + flags.Count + defects.Count;
    }

    public async Task<ParkingResult> ReportDefectAsync(
        Guid userId, SpotDefectCategory category, string description, Guid? spotId, BlockedSpotPhoto? photo = null, CancellationToken cancellationToken = default)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return ParkingResult.Failure("Parking_Defect_Error_DescriptionRequired");
        }

        if (photo is not null && photo.Content.Length > BlockedSpotPhoto.MaxBytes)
        {
            return ParkingResult.Failure("Parking_Defect_Error_PhotoTooLarge");
        }

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var settings = await LoadSettingsAsync(dbContext, cancellationToken);
            if (!settings.OversightAllowUserReports)
            {
                return ParkingResult.Failure("Parking_Defect_Error_Disabled");
            }

            var now = timeProvider.GetUtcNow();
            var report = new SpotDefectReport(userId, category, text, now, spotId);
            dbContext.SpotDefectReports.Add(report);
            if (photo is not null)
            {
                dbContext.SpotDefectPhotos.Add(
                    new SpotDefectPhoto(report.Id, photo.ContentType, photo.Content, now));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // Opened through the same ingest as every other signal rather than here, so there is still
        // exactly one place a case comes into being — and the reporter does not have to wait a
        // sweep to see their own report on the list.
        await EnsureCasesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    /// <summary>
    /// Whether the recorded plate belongs to anyone the lot knows: an employee's registered
    /// vehicle, a fleet car, or a visitor booked over the same window. Visitors are checked
    /// against the window on purpose — a guest's car is known while their booking runs and a
    /// stranger's the week after, and calling the first one an outsider would escalate exactly
    /// the case where nothing is wrong. All three are compared in the one canonical plate form,
    /// so a plate cannot match here and miss in the reviewer's own panel.
    /// </summary>
    private static async Task<bool> IsKnownPlateAsync(
        D3ParkingDbContext dbContext, string plate, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        var normalized = PlateNormalizer.Normalize(plate);
        if (normalized.Length == 0)
        {
            // Nothing legible was written down, so nothing was confirmed either way.
            return true;
        }

        if (await dbContext.CompanyVehicles.AsNoTracking()
            .AnyAsync(v => v.NormalizedPlate == normalized, cancellationToken))
        {
            return true;
        }

        // Employee and visitor plates are stored as typed, so they are normalized in memory —
        // the sets are small and the alternative is a query the provider cannot translate.
        var employeePlates = await dbContext.Users.AsNoTracking()
            .Where(u => u.LicensePlate != null)
            .Select(u => u.LicensePlate!)
            .ToListAsync(cancellationToken);
        if (employeePlates.Any(p => PlateNormalizer.Normalize(p) == normalized))
        {
            return true;
        }

        var visitorPlates = await dbContext.VisitorBookings.AsNoTracking()
            .Where(b => b.LicensePlate != null && b.Status == VisitorBookingStatus.Booked
                && b.StartUtc < endUtc && b.EndUtc > startUtc)
            .Select(b => b.LicensePlate!)
            .ToListAsync(cancellationToken);
        return visitorPlates.Any(p => PlateNormalizer.Normalize(p) == normalized);
    }

    /// <summary>
    /// Notes on every open case about this spot that the same spot has been reported again. The
    /// case that triggered it is skipped — it already says so on its own timeline.
    /// </summary>
    private static async Task AppendToOpenCasesOnSpotAsync(
        D3ParkingDbContext dbContext, Guid spotId, Guid exceptCaseId, string body, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var openCaseIds = await dbContext.OversightCases.AsNoTracking()
            .Where(c => c.SpotId == spotId && c.Id != exceptCaseId && c.Status != OversightCaseStatus.Resolved)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var caseId in openCaseIds)
        {
            dbContext.OversightCaseEvents.Add(
                OversightCaseEvent.FromSystem(caseId, OversightEventType.SignalUpdated, at, body));
        }
    }

    public async Task<int> RunDueCaseWorkAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        // Unanswered questions first: a case that comes back off the wait can then breach in the
        // same sweep, rather than waiting another one to be noticed.
        var touched = await EndStaleWaitsAsync(now, cancellationToken);
        touched += await AnnounceBreachesAsync(now, cancellationToken);
        touched += await RecordSignalUpdatesAsync(cancellationToken);
        await SendDigestIfDueAsync(now, cancellationToken);
        return touched;
    }

    /// <summary>
    /// Puts back on the desk the cases whose question was never answered. Deliberately not a
    /// verdict: the lot can tell that nobody replied, but what that means for the report is a
    /// judgement, and judgements stay with people.
    /// </summary>
    private async Task<int> EndStaleWaitsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await LoadSettingsAsync(dbContext, cancellationToken);
        var deadline = now.AddDays(-settings.OversightInfoDeadlineDays);

        var stale = await dbContext.OversightCases
            .Where(c => c.Status == OversightCaseStatus.AwaitingInfo
                && c.AwaitingSinceUtc != null && c.AwaitingSinceUtc <= deadline)
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
        {
            return 0;
        }

        foreach (var @case in stale)
        {
            @case.ResumeWork(now);
            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromSystem(
                @case.Id, OversightEventType.SignalUpdated, now,
                messages["Parking_Oversight_Reason_NoAnswer", settings.OversightInfoDeadlineDays]));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var @case in stale)
        {
            await NotifyOwnerOrReviewersAsync(dbContext, @case, NotificationLevel.Info,
                messages["Parking_Notify_OversightNoAnswer_Title"],
                messages["Parking_Notify_OversightNoAnswer_Body", @case.Number], cancellationToken);
        }

        return stale.Count;
    }

    /// <summary>
    /// Announces the deadlines that have passed, once each. The assignee hears about their own
    /// case; an unclaimed one goes to everyone who could pick it up, because there is nobody else
    /// for it to be the responsibility of.
    /// </summary>
    private async Task<int> AnnounceBreachesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var overdue = await dbContext.OversightCases
            // A case waiting on the driver is not late; the clock is stopped while it waits.
            .Where(c => c.Status != OversightCaseStatus.Resolved
                && c.Status != OversightCaseStatus.AwaitingInfo
                && c.SlaBreachedAtUtc == null
                && c.DueAtUtc != null && c.DueAtUtc <= now)
            .ToListAsync(cancellationToken);
        if (overdue.Count == 0)
        {
            return 0;
        }

        foreach (var @case in overdue)
        {
            @case.MarkSlaBreached(now);
            dbContext.OversightCaseEvents.Add(
                OversightCaseEvent.FromSystem(@case.Id, OversightEventType.SlaBreached, now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var @case in overdue)
        {
            var title = messages["Parking_Notify_OversightOverdue_Title"];
            var body = messages["Parking_Notify_OversightOverdue_Body", @case.Number];
            if (@case.AssigneeId is { } assignee)
            {
                await notifications.NotifyAsync(assignee, NotificationCategory.Administrative,
                    NotificationLevel.Warning, title, body, cancellationToken);
            }
            else
            {
                await NotifyReviewersAsync(dbContext, @case.Kind, NotificationLevel.Warning, title, body, cancellationToken);
            }
        }

        return overdue.Count;
    }

    /// <summary>
    /// Notices when the nightly scan re-measured a pair an open case is about. The test is "newer
    /// than the last time we said so", against the case's own opening and its previous re-measure
    /// notes only: comparing against the last entry of any kind would let a reviewer's comment
    /// swallow a re-measure that landed just before it, which is the one moment they most need
    /// telling. It cannot repeat itself either — the note it writes carries the very timestamp it
    /// compares against next time.
    /// </summary>
    private async Task<int> RecordSignalUpdatesAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var stale = await (
            from c in dbContext.OversightCases.AsNoTracking()
            where c.Kind == OversightCaseKind.CollusionRing && c.Status != OversightCaseStatus.Resolved
            join f in dbContext.CollusionFlags.AsNoTracking() on c.SubjectId equals f.Id
            let lastSeen = dbContext.OversightCaseEvents
                .Where(e => e.CaseId == c.Id
                    && (e.Type == OversightEventType.SignalUpdated || e.Type == OversightEventType.Opened))
                .Max(e => (DateTimeOffset?)e.OccurredAtUtc)
            where lastSeen == null || f.UpdatedAtUtc > lastSeen
            select new { CaseId = c.Id, f.UpdatedAtUtc, f.MutualInteractions, f.ConcentrationAPercent, f.ConcentrationBPercent })
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        foreach (var flag in stale)
        {
            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromSystem(
                flag.CaseId, OversightEventType.SignalUpdated, flag.UpdatedAtUtc,
                messages["Parking_Oversight_Reason_Rescan",
                    flag.MutualInteractions, flag.ConcentrationAPercent, flag.ConcentrationBPercent]));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    /// <summary>
    /// One message a day per reviewer instead of one per signal. What a reviewer needs to know each
    /// morning is the size of the pile and whether any of it is late — a notification per case turns
    /// into noise and stops being read, which is worse than not sending it.
    /// </summary>
    private async Task SendDigestIfDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await dbContext.ParkingSettings
            .FirstOrDefaultAsync(s => s.Id == ParkingSettings.SingletonId, cancellationToken);
        if (settings is null)
        {
            return;
        }

        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        if (localNow.Hour < settings.OversightDigestHourLocal)
        {
            return;
        }

        // One per local day: the marker is compared in local time, so a digest sent at 08:00 does
        // not let a second one out at 00:30 UTC the same evening.
        if (settings.LastOversightDigestUtc is { } last
            && TimeZoneInfo.ConvertTime(last, timeZone).Date == localNow.Date)
        {
            return;
        }

        // Marked before it is sent, deliberately: a mail server that goes down mid-digest costs
        // one day's message, whereas retrying it every five minutes until something succeeds costs
        // the reviewers their trust in the whole channel.
        settings.MarkOversightDigestSent(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Totals are summed per reviewer rather than per kind: someone who holds both permissions
        // has one pile of work, not two, and telling them about it twice is exactly the noise the
        // digest exists to end.
        var perReviewer = new Dictionary<Guid, (int Open, int Overdue)>();
        foreach (var kind in Enum.GetValues<OversightCaseKind>())
        {
            var open = await dbContext.OversightCases.AsNoTracking()
                .CountAsync(c => c.Kind == kind && c.Status != OversightCaseStatus.Resolved, cancellationToken);
            if (open == 0)
            {
                continue;
            }

            var overdue = await dbContext.OversightCases.AsNoTracking()
                .CountAsync(c => c.Kind == kind && c.Status != OversightCaseStatus.Resolved
                    && c.Status != OversightCaseStatus.AwaitingInfo
                    && c.DueAtUtc != null && c.DueAtUtc <= now, cancellationToken);

            foreach (var reviewerId in await ReviewerIdsAsync(dbContext, kind, cancellationToken))
            {
                var running = perReviewer.GetValueOrDefault(reviewerId);
                perReviewer[reviewerId] = (running.Open + open, running.Overdue + overdue);
            }
        }

        foreach (var (reviewerId, totals) in perReviewer)
        {
            await notifications.NotifyAsync(reviewerId, NotificationCategory.Administrative, NotificationLevel.Info,
                messages["Parking_Notify_OversightDigest_Title"],
                messages["Parking_Notify_OversightDigest_Body", totals.Open, totals.Overdue], cancellationToken);
        }
    }

    /// <summary>
    /// Everyone whose role carries the permission that may see this kind of case — the same
    /// role-claim shape the authorization layer checks, so the audience of a notification and the
    /// audience of the page can never drift apart.
    /// </summary>
    private async Task NotifyReviewersAsync(
        D3ParkingDbContext dbContext, OversightCaseKind kind, NotificationLevel level, string title, string body, CancellationToken cancellationToken)
    {
        foreach (var reviewerId in await ReviewerIdsAsync(dbContext, kind, cancellationToken))
        {
            await notifications.NotifyAsync(reviewerId, NotificationCategory.Administrative, level, title, body, cancellationToken);
        }
    }

    private static async Task<List<Guid>> ReviewerIdsAsync(
        D3ParkingDbContext dbContext, OversightCaseKind kind, CancellationToken cancellationToken)
    {
        var permission = kind == OversightCaseKind.CollusionRing
            ? Permissions.Parking.ReviewCollusion
            : Permissions.Parking.ReviewMismatches;

        var roleIds = dbContext.RoleClaims
            .Where(c => c.ClaimType == D3ParkingClaimTypes.Permission && c.ClaimValue == permission)
            .Select(c => c.RoleId);
        return await dbContext.UserRoles
            .Where(ur => roleIds.Contains(ur.RoleId))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <summary>The tunables, or their defaults when the lot has never had its settings saved.</summary>
    private static async Task<ParkingSettings> LoadSettingsAsync(D3ParkingDbContext dbContext, CancellationToken cancellationToken) =>
        await dbContext.ParkingSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == ParkingSettings.SingletonId, cancellationToken)
        ?? ParkingSettings.CreateDefault();

    public async Task<MismatchPhotoDto?> GetDefectPhotoAsync(Guid defectId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.SpotDefectPhotos.AsNoTracking()
            .Where(p => p.DefectId == defectId)
            .Select(p => new MismatchPhotoDto(p.Content, p.ContentType))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<NoShowDisputeDto?> LoadNoShowAsync(
        D3ParkingDbContext dbContext, OversightCase @case, CancellationToken cancellationToken)
    {
        var reservation = await (
            from r in dbContext.Reservations.AsNoTracking()
            where r.Id == @case.SubjectId
            join s in dbContext.ParkingSpots.AsNoTracking() on r.SpotId equals s.Id into spots
            from spot in spots.DefaultIfEmpty()
            select new { r.Id, Code = spot != null ? spot.Code : string.Empty, r.StartUtc, r.EndUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (reservation is null)
        {
            return null;
        }

        var penalties = await PenaltiesByReservationAsync(dbContext, [reservation.Id], cancellationToken);
        var (points, credits) = penalties.GetValueOrDefault(reservation.Id);
        return new NoShowDisputeDto(
            reservation.Id, reservation.Code, reservation.StartUtc, reservation.EndUtc, points, credits,
            Reversed: @case.Resolution == OversightResolution.Founded);
    }

    private static async Task<SpotDefectDto?> LoadDefectAsync(
        D3ParkingDbContext dbContext, Guid defectId, CancellationToken cancellationToken) =>
        await (from d in dbContext.SpotDefectReports.AsNoTracking()
               where d.Id == defectId
               join s in dbContext.ParkingSpots.AsNoTracking() on d.SpotId equals s.Id into spots
               from spot in spots.DefaultIfEmpty()
               join u in dbContext.Users.AsNoTracking() on d.ReporterId equals u.Id into reporters
               from reporter in reporters.DefaultIfEmpty()
               select new SpotDefectDto(
                   d.Id,
                   spot != null ? spot.Code : null,
                   d.Category,
                   d.Description,
                   reporter != null ? (reporter.DisplayName ?? reporter.Email ?? string.Empty) : string.Empty,
                   reporter != null ? reporter.Email : null,
                   // The bytes stay out of this query; the review fetches them through the endpoint.
                   dbContext.SpotDefectPhotos.Any(p => p.DefectId == d.Id),
                   d.ReportedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

    private static async Task<string> SpotCodeAsync(D3ParkingDbContext dbContext, Guid spotId, CancellationToken cancellationToken) =>
        await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.Id == spotId)
            .Select(s => s.Code)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

    /// <summary>
    /// A flagged pair is described by its numbers rather than by the two names: the notification
    /// goes to everyone holding the permission, and naming colleagues in a bell message would put
    /// an accusation in front of people before anyone has looked at it.
    /// </summary>
    private string PairLabel(int concentrationA, int concentrationB) =>
        messages["Parking_Oversight_PairLabel", Math.Min(concentrationA, concentrationB)];

    public async Task<OversightQueueDto> GetQueueAsync(OversightQuery query, OversightScope scope, CancellationToken cancellationToken = default)
    {
        if (scope.IsEmpty)
        {
            return new OversightQueueDto([], 0, 0, 0, 0, 0);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var kinds = scope.Kinds;
        var visible = dbContext.OversightCases.AsNoTracking().Where(c => kinds.Contains(c.Kind));

        // The tab counts are of the whole scope, not of what the filters left: a tab that changes
        // its number when you narrow the list underneath it stops being a place to navigate to.
        var reviewerId = query.ReviewerId;
        var counts = await visible
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Open = g.Count(c => c.Status != OversightCaseStatus.Resolved),
                Mine = g.Count(c => c.Status != OversightCaseStatus.Resolved && c.AssigneeId == reviewerId),
                Unassigned = g.Count(c => c.Status != OversightCaseStatus.Resolved && c.AssigneeId == null),
                Overdue = g.Count(c => c.Status != OversightCaseStatus.Resolved
                    && c.Status != OversightCaseStatus.AwaitingInfo
                    && c.DueAtUtc != null && c.DueAtUtc <= now),
                Total = g.Count(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var filtered = ApplyFilters(visible, query, dbContext, now);

        // Queue discipline for work, chronology for the record: an open list is ordered by the
        // deadline first — a low-priority case that has run out of time outranks a fresh critical
        // one, which is the whole point of having deadlines — then by urgency, then oldest-first so
        // nothing starves behind a stream of arrivals. "All" is a log and reads newest-touched first.
        filtered = query.View == OversightView.All
            ? filtered.OrderByDescending(c => c.UpdatedAtUtc)
            : filtered
                .OrderByDescending(c => c.Status != OversightCaseStatus.AwaitingInfo
                    && c.DueAtUtc != null && c.DueAtUtc <= now)
                .ThenByDescending(c => c.Priority)
                .ThenBy(c => c.OpenedAtUtc);

        var cases = await filtered.Take(500).ToListAsync(cancellationToken);
        var labels = await ResolveLabelsAsync(dbContext, cases, cancellationToken);
        var assigneeNames = await ResolveUserNamesAsync(
            dbContext, cases.Where(c => c.AssigneeId is not null).Select(c => c.AssigneeId!.Value), cancellationToken);

        var items = cases
            .Select(c => new OversightCaseListItemDto(
                c.Id, c.Number, c.Kind, c.Status, c.Priority,
                labels.GetValueOrDefault(c.Id, string.Empty),
                c.AssigneeId,
                c.AssigneeId is { } id ? assigneeNames.GetValueOrDefault(id) : null,
                c.OpenedAtUtc, c.UpdatedAtUtc, c.DueAtUtc, c.IsOverdue(now), c.Resolution))
            .ToList();

        return new OversightQueueDto(
            items, counts?.Open ?? 0, counts?.Mine ?? 0, counts?.Unassigned ?? 0, counts?.Overdue ?? 0, counts?.Total ?? 0);
    }

    public async Task<int> GetOpenCountAsync(OversightScope scope, CancellationToken cancellationToken = default)
    {
        if (scope.IsEmpty)
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var kinds = scope.Kinds;
        return await dbContext.OversightCases.AsNoTracking()
            .CountAsync(c => kinds.Contains(c.Kind) && c.Status != OversightCaseStatus.Resolved, cancellationToken);
    }

    public async Task<OversightCaseDetailDto?> GetCaseAsync(Guid caseId, OversightScope scope, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await dbContext.OversightCases.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);

        // A case whose kind the caller may not see reads as absent, not as forbidden: the two
        // review permissions exist so that one reviewer cannot learn the other queue's contents,
        // and "you may not see case 142" already says a case 142 exists.
        if (entity is null || !scope.Allows(entity.Kind))
        {
            return null;
        }

        var events = await dbContext.OversightCaseEvents.AsNoTracking()
            .Where(e => e.CaseId == caseId)
            .OrderBy(e => e.OccurredAtUtc)
            // Same instant, two lines: the insertion ordinal decides which came first.
            .ThenBy(e => EF.Property<long>(e, D3ParkingDbContext.OversightEventOrdinal))
            .ToListAsync(cancellationToken);

        var actorNames = await ResolveUserNamesAsync(
            dbContext, events.Where(e => e.ActorUserId is not null).Select(e => e.ActorUserId!.Value), cancellationToken);
        var assigneeName = entity.AssigneeId is { } assignee
            ? (await ResolveUserNamesAsync(dbContext, [assignee], cancellationToken)).GetValueOrDefault(assignee)
            : null;

        // The evidence lives with the signal, not on the case: read it from its own service so the
        // photo, the plate match and the concentrations have one implementation each.
        var mismatch = entity.Kind == OversightCaseKind.OccupancyMismatch
            ? await spots.GetOccupancyMismatchAsync(entity.SubjectId, cancellationToken)
            : null;
        var flag = entity.Kind == OversightCaseKind.CollusionRing
            ? await collusion.GetFlagAsync(entity.SubjectId, cancellationToken)
            : null;
        var defect = entity.Kind == OversightCaseKind.SpotDefect
            ? await LoadDefectAsync(dbContext, entity.SubjectId, cancellationToken)
            : null;
        var noShow = entity.Kind == OversightCaseKind.NoShowDispute
            ? await LoadNoShowAsync(dbContext, entity, cancellationToken)
            : null;

        var labels = await ResolveLabelsAsync(dbContext, [entity], cancellationToken);
        var partyIds = await PartyIdsAsync(dbContext, entity, cancellationToken);
        var partyNames = await ResolveUserNamesAsync(dbContext, partyIds, cancellationToken);
        var parties = partyIds
            .Select(id => new OversightPartyDto(id, partyNames.GetValueOrDefault(id, string.Empty)))
            .ToList();

        return new OversightCaseDetailDto(
            entity.Id, entity.Number, entity.Kind, entity.Status, entity.Priority,
            labels.GetValueOrDefault(entity.Id, string.Empty),
            entity.AssigneeId, assigneeName,
            entity.OpenedAtUtc, entity.UpdatedAtUtc,
            entity.DueAtUtc, entity.IsOverdue(timeProvider.GetUtcNow()),
            entity.ResolvedAtUtc, entity.Resolution, entity.ResolutionNote,
            mismatch, flag, defect, noShow, parties,
            events.Select(e => new OversightTimelineEntryDto(
                e.Id, e.Type, e.Actor,
                e.ActorUserId is { } actor ? actorNames.GetValueOrDefault(actor) : null,
                e.Body, e.Visibility, e.OccurredAtUtc)).ToList());
    }

    public Task<ParkingResult> ClaimAsync(Guid caseId, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default) =>
        MutateAsync(caseId, reviewerId, scope, (@case, dbContext, now) =>
        {
            if (@case.AssigneeId is { } holder && holder != reviewerId)
            {
                return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_AlreadyAssigned"));
            }

            if (!@case.Claim(reviewerId, now))
            {
                return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_NotOpen"));
            }

            dbContext.OversightCaseEvents.Add(
                OversightCaseEvent.FromReviewer(@case.Id, OversightEventType.Claimed, reviewerId, now));
            return Task.FromResult(ParkingResult.Success);
        }, cancellationToken);

    public Task<ParkingResult> ReleaseAsync(Guid caseId, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default) =>
        MutateAsync(caseId, reviewerId, scope, (@case, dbContext, now) =>
        {
            if (!@case.Release(now))
            {
                return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_NotOpen"));
            }

            dbContext.OversightCaseEvents.Add(
                OversightCaseEvent.FromReviewer(@case.Id, OversightEventType.Released, reviewerId, now));
            return Task.FromResult(ParkingResult.Success);
        }, cancellationToken);

    public Task<ParkingResult> AssignAsync(Guid caseId, Guid assigneeId, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default)
    {
        if (!scope.MayAssign)
        {
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_MayNotAssign"));
        }

        return MutateAsync(caseId, reviewerId, scope, async (@case, dbContext, now) =>
        {
            // Handing a case to somebody who cannot open it is a dead end that looks like progress,
            // so the target is checked against the very permission the case's kind is gated on.
            var eligible = await ReviewerIdsAsync(dbContext, @case.Kind, cancellationToken);
            if (!eligible.Contains(assigneeId))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotAReviewer");
            }

            if (!@case.Claim(assigneeId, now))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotOpen");
            }

            var names = await ResolveUserNamesAsync(dbContext, [assigneeId], cancellationToken);
            var name = names.GetValueOrDefault(assigneeId, string.Empty);

            // The name goes in the body as it reads today: the timeline is a record of what
            // happened, and a display name that changes later must not rewrite it.
            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.Assigned, reviewerId, now, name));

            if (assigneeId != reviewerId)
            {
                await notifications.NotifyAsync(assigneeId, NotificationCategory.Administrative, NotificationLevel.Info,
                    messages["Parking_Notify_OversightAssigned_Title"],
                    messages["Parking_Notify_OversightAssigned_Body", @case.Number], cancellationToken);
            }

            return ParkingResult.Success;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<OversightReviewerDto>> GetAssignableReviewersAsync(
        OversightCaseKind kind, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ids = await ReviewerIdsAsync(dbContext, kind, cancellationToken);
        var names = await ResolveUserNamesAsync(dbContext, ids, cancellationToken);
        return [.. ids
            .Select(id => new OversightReviewerDto(id, names.GetValueOrDefault(id, string.Empty)))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public Task<ParkingResult> SetPriorityAsync(Guid caseId, OversightCasePriority priority, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default) =>
        MutateAsync(caseId, reviewerId, scope, async (@case, dbContext, now) =>
        {
            var previous = @case.Priority;
            var settings = await LoadSettingsAsync(dbContext, cancellationToken);
            if (!@case.SetPriority(priority, settings.SlaFor(priority), now))
            {
                // Setting the priority it already has is not an error, just nothing to record.
                return ParkingResult.Success;
            }

            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.PriorityChanged, reviewerId, now, $"{previous} → {priority}"));
            return ParkingResult.Success;
        }, cancellationToken);

    public Task<ParkingResult> CommentAsync(Guid caseId, string body, bool visibleToParticipants, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default)
    {
        var text = body?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_EmptyComment"));
        }

        return MutateAsync(caseId, reviewerId, scope, (@case, dbContext, now) =>
        {
            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.Comment, reviewerId, now, text,
                visibleToParticipants ? OversightVisibility.Participants : OversightVisibility.Internal));
            @case.Touch(now);
            return Task.FromResult(ParkingResult.Success);
        }, cancellationToken);
    }

    public Task<ParkingResult> RecordEmailContactAsync(Guid caseId, string recipient, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default) =>
        MutateAsync(caseId, reviewerId, scope, (@case, dbContext, now) =>
        {
            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.ContactedByEmail, reviewerId, now, recipient));
            @case.Touch(now);
            return Task.FromResult(ParkingResult.Success);
        }, cancellationToken);

    public async Task<ParkingResult> ReviewVoucherAsync(Guid caseId, bool approve, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default)
    {
        Guid voucherId;
        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var entity = await dbContext.OversightCases.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
            if (entity is null || !scope.Allows(entity.Kind))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotFound");
            }

            if (entity.Kind != OversightCaseKind.OccupancyMismatch)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NoVoucher");
            }

            var pending = await dbContext.ApologyVouchers.AsNoTracking()
                .Where(v => v.SourceMismatchId == entity.SubjectId && v.Status == ApologyVoucherStatus.PendingApproval)
                .Select(v => (Guid?)v.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (pending is null)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NoVoucher");
            }

            voucherId = pending.Value;
        }

        // The economy's own guards stay where they are — a reporter cannot approve their own
        // apology, and a voucher already ruled on resolves to "already decided". Only once the
        // ruling has landed does the case record it.
        var ruling = approve
            ? await spots.ApproveVoucherAsync(voucherId, reviewerId, cancellationToken)
            : await spots.RejectVoucherAsync(voucherId, reviewerId, cancellationToken);
        if (!ruling.Succeeded)
        {
            return ruling;
        }

        return await MutateAsync(caseId, reviewerId, scope, (@case, dbContext, now) =>
        {
            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id,
                approve ? OversightEventType.VoucherApproved : OversightEventType.VoucherRejected,
                reviewerId, now, visibility: OversightVisibility.Participants));

            // Ruling on the voucher is the verdict on the report; a case left open behind an
            // approved apology would be a second, contradictory answer waiting to be given.
            if (@case.Resolve(
                    approve ? OversightResolution.Founded : OversightResolution.Unfounded,
                    note: null, reviewerId, now))
            {
                dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromSystem(
                    @case.Id, OversightEventType.Resolved, now));
            }

            return Task.FromResult(ParkingResult.Success);
        }, cancellationToken);
    }

    public Task<ParkingResult> ResolveAsync(Guid caseId, OversightResolution resolution, string? note, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default)
    {
        var text = note?.Trim();

        // Dismissal is the one verdict with a lasting side effect — the nightly scan will never
        // raise this pair again — so it has to say why. The other two can stand on the evidence.
        if (resolution == OversightResolution.Unfounded && string.IsNullOrEmpty(text))
        {
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_ReasonRequired"));
        }

        return MutateAsync(caseId, reviewerId, scope, (@case, dbContext, now) =>
        {
            if (!@case.Resolve(resolution, text, reviewerId, now))
            {
                return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_AlreadyResolved"));
            }

            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.Resolved, reviewerId, now, text));
            return Task.FromResult(ParkingResult.Success);
        }, cancellationToken);
    }

    public Task<ParkingResult> ReopenAsync(Guid caseId, string reason, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_ReasonRequired"));
        }

        return MutateAsync(caseId, reviewerId, scope, async (@case, dbContext, now) =>
        {
            var settings = await LoadSettingsAsync(dbContext, cancellationToken);
            if (!@case.Reopen(reviewerId, settings.SlaFor(@case.Priority), now))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotResolved");
            }

            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.Reopened, reviewerId, now, text));
            return ParkingResult.Success;
        }, cancellationToken);
    }

    public Task<ParkingResult> SanctionAsync(
        Guid caseId, Guid targetUserId, int points, int credits, string reason, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            // The one action here that costs somebody something says why, always. A deduction
            // nobody wrote a reason for cannot be explained to the person who took it.
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_ReasonRequired"));
        }

        if (!scope.MaySanction)
        {
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_MayNotSanction"));
        }

        var deductedPoints = Math.Max(0, points);
        var deductedCredits = Math.Max(0, credits);

        return MutateAsync(caseId, reviewerId, scope, async (@case, dbContext, now) =>
        {
            var parties = await PartyIdsAsync(dbContext, @case, cancellationToken);
            if (!parties.Contains(targetUserId))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotAParty");
            }

            if (targetUserId == reviewerId)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_SanctionSelf");
            }

            if (deductedPoints > 0 || deductedCredits > 0)
            {
                var score = await dbContext.ParkerScores.FirstOrDefaultAsync(s => s.UserId == targetUserId, cancellationToken);
                if (score is null)
                {
                    score = new ParkerScore(targetUserId);
                    dbContext.ParkerScores.Add(score);
                }

                score.ApplySanction(deductedPoints, deductedCredits, now);

                // Two ledger reasons would be inventing vocabulary for one act; the detail says
                // which case it came from, which is what anyone reading the history will ask.
                var detail = messages["Parking_Ledger_OversightSanction", @case.Number];
                if (deductedPoints > 0)
                {
                    dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                        targetUserId, IncentiveReason.ManualAdjustment, -deductedPoints, null, now, detail));
                }

                if (deductedCredits > 0)
                {
                    dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                        targetUserId, IncentiveReason.ManualAdjustment, -deductedCredits, null, now, detail));
                }
            }

            var body = deductedPoints == 0 && deductedCredits == 0
                ? text
                : $"−{deductedPoints}/−{deductedCredits}: {text}";
            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.Sanctioned, reviewerId, now, body, OversightVisibility.Participants));

            // Also on the person's own record: an oversight case is not the first place anyone
            // looks when asking what happened to an account.
            dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
                targetUserId, AccountAuditEventType.OversightSanctioned, $"admin:{reviewerId}",
                messages["Parking_Audit_OversightSanction", @case.Number, deductedPoints, deductedCredits], now));

            await notifications.NotifyAsync(targetUserId, NotificationCategory.Administrative, NotificationLevel.Warning,
                messages["Parking_Notify_OversightSanction_Title"],
                deductedPoints == 0 && deductedCredits == 0
                    ? messages["Parking_Notify_OversightWarning_Body", @case.Number, text]
                    : messages["Parking_Notify_OversightSanction_Body", @case.Number, deductedPoints, deductedCredits, text],
                cancellationToken);

            return ParkingResult.Success;
        }, cancellationToken);
    }

    /// <summary>
    /// The people this case is about. A report is about the driver who filed it; a flag is about
    /// the pair it names. A defect is about nobody, which is why it returns nothing rather than
    /// falling back to the reporter — somebody who tells you a light is out is not a party to
    /// anything.
    /// </summary>
    private static async Task<List<Guid>> PartyIdsAsync(
        D3ParkingDbContext dbContext, OversightCase @case, CancellationToken cancellationToken)
    {
        if (@case.Kind == OversightCaseKind.OccupancyMismatch)
        {
            return @case.ReporterId is { } reporter ? [reporter] : [];
        }

        if (@case.Kind != OversightCaseKind.CollusionRing)
        {
            return [];
        }

        // Projected to a pair and split here rather than in the query: an array built inside a
        // SelectMany is not something the provider can translate.
        var pair = await dbContext.CollusionFlags.AsNoTracking()
            .Where(f => f.Id == @case.SubjectId)
            .Select(f => new { f.UserA, f.UserB })
            .FirstOrDefaultAsync(cancellationToken);
        return pair is null ? [] : [pair.UserA, pair.UserB];
    }

    public Task<ParkingResult> RequestInfoAsync(Guid caseId, string question, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default)
    {
        var text = question?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_QuestionRequired"));
        }

        return MutateAsync(caseId, reviewerId, scope, async (@case, dbContext, now) =>
        {
            if (@case.ReporterId is not { } reporterId)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NoParticipant");
            }

            if (!@case.AwaitAnswer(now))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotInProgress");
            }

            // The question is the one thing on this timeline written to be read by the driver.
            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.InfoRequested, reviewerId, now, text, OversightVisibility.Participants));

            await notifications.NotifyAsync(reporterId, NotificationCategory.Administrative, NotificationLevel.Info,
                messages["Parking_Notify_OversightQuestion_Title"],
                messages["Parking_Notify_OversightQuestion_Body", @case.Number, text], cancellationToken);

            return ParkingResult.Success;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<MyOversightReportDto>> GetMyReportsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var cases = await dbContext.OversightCases.AsNoTracking()
            .Where(c => c.ReporterId == userId)
            .OrderByDescending(c => c.OpenedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);
        if (cases.Count == 0)
        {
            return [];
        }

        var ids = cases.Select(c => c.Id).ToList();
        var subjectIds = cases.Select(c => c.SubjectId).ToList();
        var spotIds = cases.Where(c => c.SpotId is not null).Select(c => c.SpotId!.Value).Distinct().ToList();

        // Only what faces the participants. The internal notes are not filtered in the UI — they
        // never leave the database, so a template that forgets to check cannot leak one.
        var timeline = await dbContext.OversightCaseEvents.AsNoTracking()
            .Where(e => ids.Contains(e.CaseId) && e.Visibility == OversightVisibility.Participants)
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => EF.Property<long>(e, D3ParkingDbContext.OversightEventOrdinal))
            .ToListAsync(cancellationToken);

        var appealed = await dbContext.OversightCaseEvents.AsNoTracking()
            .Where(e => ids.Contains(e.CaseId) && e.Type == OversightEventType.Appealed)
            .Select(e => e.CaseId)
            .ToListAsync(cancellationToken);

        var spotCodes = spotIds.Count == 0
            ? []
            : await dbContext.ParkingSpots.AsNoTracking()
                .Where(s => spotIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

        var vouchers = await dbContext.ApologyVouchers.AsNoTracking()
            .Where(v => subjectIds.Contains(v.SourceMismatchId))
            .ToDictionaryAsync(v => v.SourceMismatchId, v => v.Status, cancellationToken);

        return cases.Select(c =>
        {
            var mine = timeline.Where(e => e.CaseId == c.Id).ToList();
            var voucher = vouchers.TryGetValue(c.SubjectId, out var status) ? status : (ApologyVoucherStatus?)null;

            return new MyOversightReportDto(
                c.Id,
                c.Number,
                c.Kind,
                c.SpotId is { } spotId ? spotCodes.GetValueOrDefault(spotId, string.Empty) : string.Empty,
                c.Status,
                c.OpenedAtUtc,
                c.Resolution,
                voucher,
                c.Status == OversightCaseStatus.AwaitingInfo
                    ? mine.LastOrDefault(e => e.Type == OversightEventType.InfoRequested)?.Body
                    : null,
                // Only a rejected apology is worth disputing: the other rulings cost the driver
                // nothing, so there is nothing for them to argue against.
                CanAppeal: voucher == ApologyVoucherStatus.Rejected
                    && c.Status == OversightCaseStatus.Resolved
                    && !appealed.Contains(c.Id),
                // The reviewer stays a role rather than a name: the driver is owed the decision and
                // its reasons, not a colleague to take it up with in the corridor.
                mine.Select(e => new OversightTimelineEntryDto(
                    e.Id, e.Type, e.Actor, ActorName: null, e.Body, e.Visibility, e.OccurredAtUtc)).ToList());
        }).ToList();
    }

    public Task<ParkingResult> AddParticipantNoteAsync(Guid caseId, string body, Guid userId, CancellationToken cancellationToken = default)
    {
        var text = body?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_EmptyComment"));
        }

        return MutateAsParticipantAsync(caseId, userId, async (@case, dbContext, now) =>
        {
            if (!@case.IsOpen)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotOpen");
            }

            dbContext.OversightCaseEvents.Add(new OversightCaseEvent(
                @case.Id, OversightEventType.InfoProvided, OversightActor.Participant, now,
                userId, text, OversightVisibility.Participants));

            // Answering ends the wait; adding something unprompted only bumps the case.
            if (!@case.ResumeWork(now))
            {
                @case.Touch(now);
            }

            await NotifyOwnerOrReviewersAsync(dbContext, @case, NotificationLevel.Info,
                messages["Parking_Notify_OversightAnswer_Title"],
                messages["Parking_Notify_OversightAnswer_Body", @case.Number], cancellationToken);

            return ParkingResult.Success;
        }, cancellationToken);
    }

    public Task<ParkingResult> AppealAsync(Guid caseId, string reason, Guid userId, CancellationToken cancellationToken = default)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_ReasonRequired"));
        }

        return MutateAsParticipantAsync(caseId, userId, async (@case, dbContext, now) =>
        {
            if (@case.Status != OversightCaseStatus.Resolved)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotResolved");
            }

            var alreadyAppealed = await dbContext.OversightCaseEvents
                .AnyAsync(e => e.CaseId == @case.Id && e.Type == OversightEventType.Appealed, cancellationToken);
            if (alreadyAppealed)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_AlreadyAppealed");
            }

            var settings = await LoadSettingsAsync(dbContext, cancellationToken);
            if (!@case.Reopen(reviewerId: Guid.Empty, settings.SlaFor(@case.Priority), now))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotResolved");
            }

            // Reopen hands the case to whoever reopened it; an appeal has no reviewer yet, and it
            // must not land back with the person whose ruling is being disputed.
            @case.Release(now);

            dbContext.OversightCaseEvents.Add(new OversightCaseEvent(
                @case.Id, OversightEventType.Appealed, OversightActor.Participant, now,
                userId, text, OversightVisibility.Participants));

            await NotifyReviewersAsync(dbContext, @case.Kind, NotificationLevel.Warning,
                messages["Parking_Notify_OversightAppeal_Title"],
                messages["Parking_Notify_OversightAppeal_Body", @case.Number], cancellationToken);

            return ParkingResult.Success;
        }, cancellationToken);
    }

    /// <summary>The ledger reasons the no-show sweep writes; what a upheld dispute has to give back.</summary>
    private static readonly IncentiveReason[] NoShowPenaltyReasons =
        [IncentiveReason.NoShowPenalty, IncentiveReason.QueueNoShowPenalty, IncentiveReason.QueueNoShowFine];

    public async Task<IReadOnlyList<NoShowDisputeDto>> GetDisputableNoShowsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await LoadSettingsAsync(dbContext, cancellationToken);
        var since = timeProvider.GetUtcNow().AddDays(-settings.OversightDisputeWindowDays);

        var reservations = await (
            from r in dbContext.Reservations.AsNoTracking()
            where r.UserId == userId && r.Status == ReservationStatus.NoShow && r.StartUtc >= since
                && !dbContext.OversightCases.Any(c =>
                    c.Kind == OversightCaseKind.NoShowDispute && c.SubjectId == r.Id)
            join s in dbContext.ParkingSpots.AsNoTracking() on r.SpotId equals s.Id into spots
            from spot in spots.DefaultIfEmpty()
            orderby r.StartUtc descending
            select new { r.Id, Code = spot != null ? spot.Code : string.Empty, r.StartUtc, r.EndUtc })
            .Take(20)
            .ToListAsync(cancellationToken);

        if (reservations.Count == 0)
        {
            return [];
        }

        var ids = reservations.Select(r => r.Id).ToList();
        var penalties = await PenaltiesByReservationAsync(dbContext, ids, cancellationToken);

        return [.. reservations.Select(r =>
        {
            var (points, credits) = penalties.GetValueOrDefault(r.Id);
            return new NoShowDisputeDto(r.Id, r.Code, r.StartUtc, r.EndUtc, points, credits, Reversed: false);
        })];
    }

    public async Task<ParkingResult> DisputeNoShowAsync(Guid reservationId, string reason, Guid userId, CancellationToken cancellationToken = default)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return ParkingResult.Failure("Parking_Oversight_Error_ReasonRequired");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await LoadSettingsAsync(dbContext, cancellationToken);
        var now = timeProvider.GetUtcNow();

        var reservation = await dbContext.Reservations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
        if (reservation is null || reservation.UserId != userId || reservation.Status != ReservationStatus.NoShow)
        {
            return ParkingResult.Failure("Parking_Oversight_Error_NotDisputable");
        }

        if (reservation.StartUtc < now.AddDays(-settings.OversightDisputeWindowDays))
        {
            return ParkingResult.Failure("Parking_Oversight_Error_DisputeWindowClosed");
        }

        // Opened here rather than by the ingest: the ingest reconciles signals the lot itself
        // raised, and a dispute is not one of those — the reservation has sat there all along and
        // nothing about it changed except that somebody decided to argue.
        var opened = new OversightCase(
            OversightCaseKind.NoShowDispute, reservationId, now,
            spotId: reservation.SpotId, reporterId: userId);
        dbContext.OversightCases.Add(opened);
        opened.OpenAt(OversightCasePriority.Normal, settings.SlaFor(OversightCasePriority.Normal));
        dbContext.OversightCaseEvents.Add(
            OversightCaseEvent.FromSystem(opened.Id, OversightEventType.Opened, now));
        dbContext.OversightCaseEvents.Add(new OversightCaseEvent(
            opened.Id, OversightEventType.InfoProvided, OversightActor.Participant, now,
            userId, text, OversightVisibility.Participants));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
        {
            // The (Kind, SubjectId) index is what makes "once per reservation" true even against
            // a double submit; it does not need a second check in front of it.
            return ParkingResult.Failure("Parking_Oversight_Error_AlreadyDisputed");
        }

        await NotifyReviewersAsync(dbContext, OversightCaseKind.NoShowDispute, NotificationLevel.Info,
            messages["Parking_Notify_OversightDispute_Title"],
            messages["Parking_Notify_OversightDispute_Body", opened.Number], cancellationToken);

        return ParkingResult.Success;
    }

    public Task<ParkingResult> ReverseNoShowAsync(Guid caseId, Guid reviewerId, OversightScope scope, CancellationToken cancellationToken = default) =>
        MutateAsync(caseId, reviewerId, scope, async (@case, dbContext, now) =>
        {
            if (@case.Kind != OversightCaseKind.NoShowDispute)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotADispute");
            }

            var reservation = await dbContext.Reservations.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == @case.SubjectId, cancellationToken);
            if (reservation is null)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotDisputable");
            }

            var penalties = await PenaltiesByReservationAsync(dbContext, [reservation.Id], cancellationToken);
            var (points, credits) = penalties.GetValueOrDefault(reservation.Id);
            if (points == 0 && credits == 0)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NothingToReverse");
            }

            var score = await dbContext.ParkerScores.FirstOrDefaultAsync(s => s.UserId == reservation.UserId, cancellationToken);
            if (score is null)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NothingToReverse");
            }

            // Exactly what was taken, no more: the amounts come from the ledger, so this can undo
            // a mistake but can never be used to hand somebody a standing they never had.
            score.ReverseNoShowPenalty(points, credits, now);

            var detail = messages["Parking_Ledger_NoShowReversed", @case.Number];
            if (points > 0)
            {
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    reservation.UserId, IncentiveReason.ManualAdjustment, points, reservation.Id, now, detail));
            }

            if (credits > 0)
            {
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    reservation.UserId, IncentiveReason.ManualAdjustment, credits, reservation.Id, now, detail));
            }

            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.Sanctioned, reviewerId, now,
                messages["Parking_Oversight_Reason_Reversed", points, credits],
                OversightVisibility.Participants));

            if (!@case.Resolve(OversightResolution.Founded, note: null, reviewerId, now))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_AlreadyResolved");
            }

            await notifications.NotifyAsync(reservation.UserId, NotificationCategory.Administrative, NotificationLevel.Info,
                messages["Parking_Notify_NoShowReversed_Title"],
                messages["Parking_Notify_NoShowReversed_Body", @case.Number, points, credits], cancellationToken);

            return ParkingResult.Success;
        }, cancellationToken);

    /// <summary>
    /// What the no-show sweep took per reservation, read back from the ledger and returned as
    /// positive amounts to give back. The two are kept apart because they restore to different
    /// places: reputation to the score, the fine to the wallet.
    /// </summary>
    private static async Task<Dictionary<Guid, (int Points, int Credits)>> PenaltiesByReservationAsync(
        D3ParkingDbContext dbContext, IReadOnlyList<Guid> reservationIds, CancellationToken cancellationToken)
    {
        var entries = await dbContext.PointsLedgerEntries.AsNoTracking()
            .Where(e => e.ReservationId != null && reservationIds.Contains(e.ReservationId.Value)
                && NoShowPenaltyReasons.Contains(e.Reason))
            .Select(e => new { ReservationId = e.ReservationId!.Value, e.Reason, e.Points })
            .ToListAsync(cancellationToken);

        // The sweep writes them negative; a reversal hands the same magnitude back.
        return entries
            .GroupBy(e => e.ReservationId)
            .ToDictionary(
                g => g.Key,
                g => (
                    Points: -g.Where(e => e.Reason is IncentiveReason.NoShowPenalty or IncentiveReason.QueueNoShowPenalty)
                        .Sum(e => e.Points),
                    Credits: -g.Where(e => e.Reason == IncentiveReason.QueueNoShowFine).Sum(e => e.Points)));
    }

    public Task<ParkingResult> WithdrawAsync(Guid caseId, string reason, Guid userId, CancellationToken cancellationToken = default)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_ReasonRequired"));
        }

        return MutateAsParticipantAsync(caseId, userId, async (@case, dbContext, now) =>
        {
            if (!@case.IsOpen)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotOpen");
            }

            // The apology goes with the report. Relinquish, not Reject: nobody ruled on anything,
            // and the holder giving up their own voucher is the one move on it that needs no
            // guarding — it can only ever cost them.
            var voucher = await dbContext.ApologyVouchers
                .FirstOrDefaultAsync(v => v.SourceMismatchId == @case.SubjectId
                    && v.Status == ApologyVoucherStatus.PendingApproval, cancellationToken);
            voucher?.Relinquish(now);

            dbContext.OversightCaseEvents.Add(new OversightCaseEvent(
                @case.Id, OversightEventType.Withdrawn, OversightActor.Participant, now,
                userId, text, OversightVisibility.Participants));

            // A report taken back did not hold up; a defect taken back was real and is over.
            var resolution = @case.Kind == OversightCaseKind.OccupancyMismatch
                ? OversightResolution.Unfounded
                : OversightResolution.NoActionNeeded;
            if (!@case.Resolve(resolution, text, reviewerId: userId, now))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_AlreadyResolved");
            }

            // Resolve credits the case to whoever closed it when nobody held it; the driver is not
            // a reviewer and must not end up looking like the one who worked it.
            @case.ClearAssigneeIf(userId);

            await NotifyOwnerOrReviewersAsync(dbContext, @case, NotificationLevel.Info,
                messages["Parking_Notify_OversightWithdrawn_Title"],
                messages["Parking_Notify_OversightWithdrawn_Body", @case.Number], cancellationToken);

            return ParkingResult.Success;
        }, cancellationToken);
    }

    /// <summary>Whoever holds the case, or everyone who could pick it up when nobody does.</summary>
    private async Task NotifyOwnerOrReviewersAsync(
        D3ParkingDbContext dbContext, OversightCase @case, NotificationLevel level, string title, string body, CancellationToken cancellationToken)
    {
        if (@case.AssigneeId is { } assignee)
        {
            await notifications.NotifyAsync(assignee, NotificationCategory.Administrative, level, title, body, cancellationToken);
        }
        else
        {
            await NotifyReviewersAsync(dbContext, @case.Kind, level, title, body, cancellationToken);
        }
    }

    /// <summary>
    /// The write path for the driver's own actions. The gate is ownership of the report rather
    /// than a permission, and a case that is not theirs reads as absent — the same answer someone
    /// gets for a case number they invented.
    /// </summary>
    private Task<ParkingResult> MutateAsParticipantAsync(
        Guid caseId,
        Guid userId,
        Func<OversightCase, D3ParkingDbContext, DateTimeOffset, Task<ParkingResult>> action,
        CancellationToken cancellationToken) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var entity = await dbContext.OversightCases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
            if (entity is null || entity.ReporterId != userId)
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotFound");
            }

            var result = await action(entity, dbContext, timeProvider.GetUtcNow());
            if (!result.Succeeded)
            {
                return result;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return result;
        }, cancellationToken);

    /// <summary>
    /// One write path for every case action: load inside the scope, let the caller decide, save.
    /// Retried on a lost race so the second reviewer's click re-reads and meets the guard that
    /// tells them the case was already decided, instead of silently overwriting the first ruling.
    /// </summary>
    private Task<ParkingResult> MutateAsync(
        Guid caseId,
        Guid reviewerId,
        OversightScope scope,
        Func<OversightCase, D3ParkingDbContext, DateTimeOffset, Task<ParkingResult>> action,
        CancellationToken cancellationToken) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var entity = await dbContext.OversightCases.FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
            if (entity is null || !scope.Allows(entity.Kind))
            {
                return ParkingResult.Failure("Parking_Oversight_Error_NotFound");
            }

            var result = await action(entity, dbContext, timeProvider.GetUtcNow());
            if (!result.Succeeded)
            {
                return result;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return result;
        }, cancellationToken);

    /// <summary>
    /// The one line that says what a case is about: the spot code for a report, the two names for
    /// a flagged pair. Resolved in bulk here rather than per row, and left empty when the subject
    /// is gone — a case outlives the account or the spot it was about.
    /// </summary>
    private static async Task<Dictionary<Guid, string>> ResolveLabelsAsync(
        D3ParkingDbContext dbContext, IReadOnlyList<OversightCase> cases, CancellationToken cancellationToken)
    {
        var labels = new Dictionary<Guid, string>();
        if (cases.Count == 0)
        {
            return labels;
        }

        var spotIds = cases.Where(c => c.SpotId is not null).Select(c => c.SpotId!.Value).Distinct().ToList();
        var spotCodes = spotIds.Count == 0
            ? []
            : await dbContext.ParkingSpots.AsNoTracking()
                .Where(s => spotIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);

        var flagIds = cases
            .Where(c => c.Kind == OversightCaseKind.CollusionRing)
            .Select(c => c.SubjectId)
            .Distinct()
            .ToList();
        var pairs = flagIds.Count == 0
            ? []
            : await dbContext.CollusionFlags.AsNoTracking()
                .Where(f => flagIds.Contains(f.Id))
                .ToDictionaryAsync(f => f.Id, f => (f.UserA, f.UserB), cancellationToken);

        var names = await ResolveUserNamesAsync(
            dbContext, pairs.Values.SelectMany(p => new[] { p.UserA, p.UserB }), cancellationToken);

        // A defect about the lot itself has no spot to name it by, so it borrows the opening words
        // of what was reported — "the barrier does not read cards" says more in a list than a dash.
        var defectIds = cases
            .Where(c => c.Kind == OversightCaseKind.SpotDefect && c.SpotId is null)
            .Select(c => c.SubjectId)
            .Distinct()
            .ToList();
        var defectSummaries = defectIds.Count == 0
            ? []
            : await dbContext.SpotDefectReports.AsNoTracking()
                .Where(d => defectIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => Summarize(d.Description), cancellationToken);

        foreach (var @case in cases)
        {
            labels[@case.Id] = @case.Kind switch
            {
                OversightCaseKind.CollusionRing when pairs.TryGetValue(@case.SubjectId, out var pair) =>
                    string.Join(PairSeparator, names.GetValueOrDefault(pair.UserA, string.Empty), names.GetValueOrDefault(pair.UserB, string.Empty)),
                OversightCaseKind.CollusionRing => string.Empty,
                _ when @case.SpotId is { } spotId => spotCodes.GetValueOrDefault(spotId, string.Empty),
                OversightCaseKind.SpotDefect => defectSummaries.GetValueOrDefault(@case.SubjectId, string.Empty),
                _ => string.Empty,
            };
        }

        return labels;
    }

    private const int SummaryLength = 60;

    private static string Summarize(string text) =>
        text.Length <= SummaryLength ? text : text[..SummaryLength].TrimEnd() + "…";

    private static async Task<Dictionary<Guid, string>> ResolveUserNamesAsync(
        D3ParkingDbContext dbContext, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await dbContext.Users.AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Email ?? string.Empty, cancellationToken);
    }

    private static IQueryable<OversightCase> ApplyFilters(
        IQueryable<OversightCase> cases, OversightQuery query, D3ParkingDbContext dbContext, DateTimeOffset now)
    {
        cases = query.View switch
        {
            OversightView.Open => cases.Where(c => c.Status != OversightCaseStatus.Resolved),
            OversightView.Mine => cases.Where(c => c.Status != OversightCaseStatus.Resolved && c.AssigneeId == query.ReviewerId),
            OversightView.Unassigned => cases.Where(c => c.Status != OversightCaseStatus.Resolved && c.AssigneeId == null),
            OversightView.Overdue => cases.Where(c => c.Status != OversightCaseStatus.Resolved
                && c.Status != OversightCaseStatus.AwaitingInfo
                && c.DueAtUtc != null && c.DueAtUtc <= now),
            _ => cases,
        };

        if (query.Kind is { } kind)
        {
            cases = cases.Where(c => c.Kind == kind);
        }

        if (query.Priority is { } priority)
        {
            cases = cases.Where(c => c.Priority == priority);
        }

        var term = query.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // A reviewer searches for the handle they were given: a case number read out loud, a
            // spot code off a report, or the name of someone the case is about.
            var number = int.TryParse(term, out var parsed) ? parsed : (int?)null;
            cases = cases.Where(c =>
                (number != null && c.Number == number)
                || dbContext.ParkingSpots.Any(s => s.Id == c.SpotId && s.Code.Contains(term))
                || dbContext.Users.Any(u => u.Id == c.ReporterId
                    && ((u.DisplayName != null && u.DisplayName.Contains(term)) || (u.Email != null && u.Email.Contains(term))))
                || dbContext.CollusionFlags.Any(f => f.Id == c.SubjectId
                    && dbContext.Users.Any(u => (u.Id == f.UserA || u.Id == f.UserB)
                        && ((u.DisplayName != null && u.DisplayName.Contains(term)) || (u.Email != null && u.Email.Contains(term))))));
        }

        return cases;
    }
}
