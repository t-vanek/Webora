using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Notifications;
using D3Parking.Application.Oversight;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Oversight;
using D3Parking.Domain.Parking;
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
            .Select(m => new { m.Id, m.SpotId, m.ReporterId, m.ReportedAtUtc })
            .OrderBy(m => m.ReportedAtUtc)
            .ToListAsync(cancellationToken);

        var flags = await dbContext.CollusionFlags.AsNoTracking()
            .Where(f => !dbContext.OversightCases.Any(c =>
                c.Kind == OversightCaseKind.CollusionRing && c.SubjectId == f.Id))
            .Select(f => new { f.Id, f.DetectedAtUtc, f.MutualInteractions, f.ConcentrationAPercent, f.ConcentrationBPercent })
            .OrderBy(f => f.DetectedAtUtc)
            .ToListAsync(cancellationToken);

        if (mismatches.Count == 0 && flags.Count == 0)
        {
            return 0;
        }

        var settings = await LoadSettingsAsync(dbContext, cancellationToken);
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

            var priority = repeats >= settings.OversightRecurrenceThreshold * 2 ? OversightCasePriority.Critical
                : repeats >= settings.OversightRecurrenceThreshold ? OversightCasePriority.High
                : OversightCasePriority.Normal;
            opened.OpenAt(priority, settings.SlaFor(priority));

            if (priority != OversightCasePriority.Normal)
            {
                dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromSystem(
                    opened.Id, OversightEventType.Escalated, mismatch.ReportedAtUtc,
                    messages["Parking_Oversight_Reason_Recurrence", repeats, settings.OversightRecurrenceWindowDays]));

                // Tell whoever is already holding an open case on this spot that it happened
                // again — the pattern is the point, and they would otherwise never see the rest of it.
                await AppendToOpenCasesOnSpotAsync(dbContext, mismatch.SpotId, opened.Id,
                    messages["Parking_Oversight_Reason_Recurrence", repeats, settings.OversightRecurrenceWindowDays],
                    mismatch.ReportedAtUtc, cancellationToken);
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

        await dbContext.SaveChangesAsync(cancellationToken);

        // Only the ones that will not wait for tomorrow's digest.
        foreach (var (@case, subject) in announce.Where(a => a.Case.Priority >= OversightCasePriority.High))
        {
            await NotifyReviewersAsync(dbContext, @case.Kind, NotificationLevel.Warning,
                messages["Parking_Notify_OversightNew_Title"],
                messages["Parking_Notify_OversightNew_Body", @case.Number, subject], cancellationToken);
        }

        return mismatches.Count + flags.Count;
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
        var touched = await AnnounceBreachesAsync(now, cancellationToken);
        touched += await RecordSignalUpdatesAsync(cancellationToken);
        await SendDigestIfDueAsync(now, cancellationToken);
        return touched;
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
            .Where(c => c.Status != OversightCaseStatus.Resolved
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
                Overdue = g.Count(c => c.Status != OversightCaseStatus.Resolved && c.DueAtUtc != null && c.DueAtUtc <= now),
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
                .OrderByDescending(c => c.DueAtUtc != null && c.DueAtUtc <= now)
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

        var labels = await ResolveLabelsAsync(dbContext, [entity], cancellationToken);

        return new OversightCaseDetailDto(
            entity.Id, entity.Number, entity.Kind, entity.Status, entity.Priority,
            labels.GetValueOrDefault(entity.Id, string.Empty),
            entity.AssigneeId, assigneeName,
            entity.OpenedAtUtc, entity.UpdatedAtUtc,
            entity.DueAtUtc, entity.IsOverdue(timeProvider.GetUtcNow()),
            entity.ResolvedAtUtc, entity.Resolution, entity.ResolutionNote,
            mismatch, flag,
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

        foreach (var @case in cases)
        {
            labels[@case.Id] = @case.Kind switch
            {
                OversightCaseKind.OccupancyMismatch =>
                    @case.SpotId is { } spotId ? spotCodes.GetValueOrDefault(spotId, string.Empty) : string.Empty,
                OversightCaseKind.CollusionRing when pairs.TryGetValue(@case.SubjectId, out var pair) =>
                    string.Join(PairSeparator, names.GetValueOrDefault(pair.UserA, string.Empty), names.GetValueOrDefault(pair.UserB, string.Empty)),
                _ => string.Empty,
            };
        }

        return labels;
    }

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
            OversightView.Overdue => cases.Where(c => c.Status != OversightCaseStatus.Resolved && c.DueAtUtc != null && c.DueAtUtc <= now),
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
