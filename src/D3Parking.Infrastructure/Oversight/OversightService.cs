using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Oversight;
using D3Parking.Application.Parking;
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
            .ToListAsync(cancellationToken);

        var flags = await dbContext.CollusionFlags.AsNoTracking()
            .Where(f => !dbContext.OversightCases.Any(c =>
                c.Kind == OversightCaseKind.CollusionRing && c.SubjectId == f.Id))
            .Select(f => new { f.Id, f.DetectedAtUtc })
            .ToListAsync(cancellationToken);

        if (mismatches.Count == 0 && flags.Count == 0)
        {
            return 0;
        }

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
        }

        foreach (var flag in flags)
        {
            var opened = new OversightCase(OversightCaseKind.CollusionRing, flag.Id, flag.DetectedAtUtc);
            dbContext.OversightCases.Add(opened);
            dbContext.OversightCaseEvents.Add(
                OversightCaseEvent.FromSystem(opened.Id, OversightEventType.Opened, flag.DetectedAtUtc));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return mismatches.Count + flags.Count;
    }

    public async Task<OversightQueueDto> GetQueueAsync(OversightQuery query, OversightScope scope, CancellationToken cancellationToken = default)
    {
        if (scope.IsEmpty)
        {
            return new OversightQueueDto([], 0, 0, 0, 0);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
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
                Total = g.Count(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var filtered = ApplyFilters(visible, query, dbContext);

        // Queue discipline for work, chronology for the record: an open list is ordered by urgency
        // and then oldest-first, so nothing starves behind a stream of fresh arrivals, while "all"
        // is a log and reads newest-touched first.
        filtered = query.View == OversightView.All
            ? filtered.OrderByDescending(c => c.UpdatedAtUtc)
            : filtered.OrderByDescending(c => c.Priority).ThenBy(c => c.OpenedAtUtc);

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
                c.OpenedAtUtc, c.UpdatedAtUtc, c.Resolution))
            .ToList();

        return new OversightQueueDto(items, counts?.Open ?? 0, counts?.Mine ?? 0, counts?.Unassigned ?? 0, counts?.Total ?? 0);
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
            entity.OpenedAtUtc, entity.UpdatedAtUtc, entity.ResolvedAtUtc,
            entity.Resolution, entity.ResolutionNote,
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
        MutateAsync(caseId, reviewerId, scope, (@case, dbContext, now) =>
        {
            var previous = @case.Priority;
            if (!@case.SetPriority(priority, now))
            {
                // Setting the priority it already has is not an error, just nothing to record.
                return Task.FromResult(ParkingResult.Success);
            }

            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.PriorityChanged, reviewerId, now, $"{previous} → {priority}"));
            return Task.FromResult(ParkingResult.Success);
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

        return MutateAsync(caseId, reviewerId, scope, (@case, dbContext, now) =>
        {
            if (!@case.Reopen(reviewerId, now))
            {
                return Task.FromResult(ParkingResult.Failure("Parking_Oversight_Error_NotResolved"));
            }

            dbContext.OversightCaseEvents.Add(OversightCaseEvent.FromReviewer(
                @case.Id, OversightEventType.Reopened, reviewerId, now, text));
            return Task.FromResult(ParkingResult.Success);
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
        IQueryable<OversightCase> cases, OversightQuery query, D3ParkingDbContext dbContext)
    {
        cases = query.View switch
        {
            OversightView.Open => cases.Where(c => c.Status != OversightCaseStatus.Resolved),
            OversightView.Mine => cases.Where(c => c.Status != OversightCaseStatus.Resolved && c.AssigneeId == query.ReviewerId),
            OversightView.Unassigned => cases.Where(c => c.Status != OversightCaseStatus.Resolved && c.AssigneeId == null),
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
