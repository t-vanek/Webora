using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Oversight;
using D3Parking.Application.Parking;
using D3Parking.Domain.Oversight;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Oversight;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// The oversight desk: signals become cases, a case has exactly one owner and one verdict, and the
/// two review permissions still keep the kinds apart. Runs against the SQL Server from
/// ConnectionStrings__SqlServer (skipped without it); a dedicated database per fixture.
/// </summary>
[TestFixture]
[NonParallelizable]
public class OversightCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 20, 0, 0, TimeSpan.Zero);

    /// <summary>Sees both kinds — what an administrator holds.</summary>
    private static readonly OversightScope Both = OversightScope.From(true, true);

    /// <summary>Sees reports only — the split that keeps photographs of third parties' cars in one queue.</summary>
    private static readonly OversightScope MismatchesOnly = OversightScope.From(true, false);

    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private OversightService _oversight = null!;
    private ReservationService _reservations = null!;
    private CollusionService _collusion = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the oversight tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_OversightCaseTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var factory = new TestDbContextFactory(_options);
        var time = new FixedTimeProvider(Now);
        var notifications = new RecordingNotificationService();
        var messages = new PassthroughLocalizer<ParkingMessages>();
        var siteSettings = new FakeSiteSettings();

        var spots = new ParkingSpotService(factory, notifications, siteSettings, time, messages);
        _collusion = new CollusionService(factory, time, notifications, messages);
        _reservations = new ReservationService(
            factory, new FakeParkingSettings(IncentivePolicy.Default), siteSettings, time, notifications, messages);
        _oversight = new OversightService(factory, spots, _collusion, time);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_options is not null)
        {
            await using var dbContext = new D3ParkingDbContext(_options);
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task Ingest_opens_one_case_per_signal_and_runs_again_harmlessly()
    {
        var mismatch = await SeedMismatchAsync("C-01");

        var first = await _oversight.EnsureCasesAsync();
        var second = await _oversight.EnsureCasesAsync();

        Assert.That(first, Is.GreaterThanOrEqualTo(1));
        Assert.That(second, Is.Zero, "A second pass must find nothing left to open.");

        await using var db = new D3ParkingDbContext(_options);
        var opened = await db.OversightCases
            .SingleAsync(c => c.Kind == OversightCaseKind.OccupancyMismatch && c.SubjectId == mismatch.Id);
        Assert.That(opened.Status, Is.EqualTo(OversightCaseStatus.New));
        Assert.That(opened.AssigneeId, Is.Null);
        Assert.That(opened.SpotId, Is.EqualTo(mismatch.SpotId));
        Assert.That(opened.OpenedAtUtc, Is.EqualTo(mismatch.ReportedAtUtc),
            "A case is dated to when the signal was raised, not to when the ingest noticed.");
        Assert.That(opened.Number, Is.GreaterThan(0), "Every case gets a number reviewers can quote.");

        var timeline = await db.OversightCaseEvents.Where(e => e.CaseId == opened.Id).ToListAsync();
        Assert.That(timeline.Select(e => e.Type), Is.EquivalentTo(new[] { OversightEventType.Opened }));
        Assert.That(timeline[0].Actor, Is.EqualTo(OversightActor.System));
    }

    [Test]
    public async Task A_case_of_a_kind_the_scope_hides_is_absent_rather_than_forbidden()
    {
        var flagId = await SeedFlagAsync();
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.CollusionRing, flagId);

        var hidden = await _oversight.GetCaseAsync(caseId, MismatchesOnly);
        var queue = await _oversight.GetQueueAsync(new OversightQuery(Guid.NewGuid()), MismatchesOnly);

        Assert.That(hidden, Is.Null, "Reading a case outside the scope must not reveal that it exists.");
        Assert.That(queue.Cases.Select(c => c.Id), Does.Not.Contain(caseId));
        Assert.That(queue.Cases.Select(c => c.Kind), Has.All.EqualTo(OversightCaseKind.OccupancyMismatch));
        Assert.That(await _oversight.GetOpenCountAsync(MismatchesOnly),
            Is.EqualTo(queue.OpenCount), "The badge counts the same cases the queue lists.");

        // And every write is bounded by the same scope, not just the reads.
        var claim = await _oversight.ClaimAsync(caseId, Guid.NewGuid(), MismatchesOnly);
        Assert.That(claim.Succeeded, Is.False);
        Assert.That(claim.Errors, Does.Contain("Parking_Oversight_Error_NotFound"));
    }

    [Test]
    public async Task A_case_has_one_owner()
    {
        var mismatch = await SeedMismatchAsync("C-02");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var mine = await _oversight.ClaimAsync(caseId, first, Both);
        var theirs = await _oversight.ClaimAsync(caseId, second, Both);

        Assert.That(mine.Succeeded, Is.True, mine.Errors.FirstOrDefault());
        Assert.That(theirs.Succeeded, Is.False, "The second reviewer must be told, not silently win.");
        Assert.That(theirs.Errors, Does.Contain("Parking_Oversight_Error_AlreadyAssigned"));

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(detail!.AssigneeId, Is.EqualTo(first));
        Assert.That(detail.Status, Is.EqualTo(OversightCaseStatus.InProgress));

        // Released, it is anyone's again.
        Assert.That((await _oversight.ReleaseAsync(caseId, first, Both)).Succeeded, Is.True);
        Assert.That((await _oversight.ClaimAsync(caseId, second, Both)).Succeeded, Is.True);
    }

    [Test]
    public async Task Notes_share_one_timeline_and_only_the_marked_ones_face_the_participants()
    {
        var mismatch = await SeedMismatchAsync("C-03");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);
        var reviewer = Guid.NewGuid();

        Assert.That((await _oversight.CommentAsync(caseId, "  ", visibleToParticipants: false, reviewer, Both)).Errors,
            Does.Contain("Parking_Oversight_Error_EmptyComment"));
        await _oversight.CommentAsync(caseId, "Auto tam pořád stojí.", visibleToParticipants: false, reviewer, Both);
        await _oversight.CommentAsync(caseId, "Řešíme, ozveme se.", visibleToParticipants: true, reviewer, Both);
        await _oversight.RecordEmailContactAsync(caseId, "kolega@d3parking.local", reviewer, Both);

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        var notes = detail!.Timeline.Where(e => e.Type == OversightEventType.Comment).ToList();

        Assert.That(notes, Has.Count.EqualTo(2));
        Assert.That(notes[0].Visibility, Is.EqualTo(OversightVisibility.Internal), "A note is internal unless said otherwise.");
        Assert.That(notes[1].Visibility, Is.EqualTo(OversightVisibility.Participants));
        Assert.That(notes.Select(n => n.Actor), Has.All.EqualTo(OversightActor.Reviewer));

        var contact = detail.Timeline.Single(e => e.Type == OversightEventType.ContactedByEmail);
        Assert.That(contact.Body, Is.EqualTo("kolega@d3parking.local"),
            "The follow-up happens in a mail client; the only record that anyone asked is this one.");
        Assert.That(detail.Timeline.Select(e => e.OccurredAtUtc), Is.Ordered, "The history reads as a story.");
    }

    [Test]
    public async Task Ruling_on_the_voucher_closes_the_case_with_it()
    {
        var reviewer = await SeedUserAsync();
        var (driver, reservation) = await SeedBlockedReservationAsync("C-04");
        var report = await _reservations.ReportBlockedSpotAsync(driver, reservation.Id, relocate: false, Photo(4));
        Assert.That(report.VoucherGranted, Is.True, report.Error);

        await _oversight.EnsureCasesAsync();
        var mismatchId = await MismatchIdOfAsync(driver);
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatchId);

        var ruling = await _oversight.ReviewVoucherAsync(caseId, approve: true, reviewer, Both);
        Assert.That(ruling.Succeeded, Is.True, ruling.Errors.FirstOrDefault());

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(detail!.Status, Is.EqualTo(OversightCaseStatus.Resolved),
            "Approving the apology is the verdict on the report; a case left open would be a second answer.");
        Assert.That(detail.Resolution, Is.EqualTo(OversightResolution.Founded));
        Assert.That(detail.Timeline.Select(e => e.Type), Does.Contain(OversightEventType.VoucherApproved));
        Assert.That(detail.Mismatch!.Voucher!.Status, Is.EqualTo(ApologyVoucherStatus.Approved));

        // The economy's own guard still stands in front of the case action.
        var again = await _oversight.ReviewVoucherAsync(caseId, approve: false, reviewer, Both);
        Assert.That(again.Succeeded, Is.False);
        Assert.That(again.Errors, Does.Contain("Parking_Oversight_Error_NoVoucher"));
    }

    [Test]
    public async Task Dismissing_a_pair_keeps_the_nightly_scan_from_raising_it_again()
    {
        var (owner, guest) = await SeedSharingPairAsync("C-05", interactions: 6);
        await SeedSettingsAsync();

        Assert.That(await _collusion.ScanAsync(), Is.EqualTo(1), "The pair is concentrated enough to flag.");
        await _oversight.EnsureCasesAsync();
        var flagId = await FlagIdOfAsync(owner, guest);
        var caseId = await CaseIdForAsync(OversightCaseKind.CollusionRing, flagId);

        var withoutReason = await _oversight.ResolveAsync(caseId, OversightResolution.Unfounded, "  ", Guid.NewGuid(), Both);
        Assert.That(withoutReason.Errors, Does.Contain("Parking_Oversight_Error_ReasonRequired"),
            "The one verdict that silences the scan for good has to say why.");

        var dismissed = await _oversight.ResolveAsync(
            caseId, OversightResolution.Unfounded, "Sdílejí kancelář, jezdí spolu.", Guid.NewGuid(), Both);
        Assert.That(dismissed.Succeeded, Is.True, dismissed.Errors.FirstOrDefault());

        // A fresh scan window, same interactions: the flag must not be re-measured or re-raised.
        var before = await FlagUpdatedAtAsync(flagId);
        await ClearScanTimestampAsync();
        Assert.That(await _collusion.ScanAsync(), Is.Zero, "A dismissed pair must not come back.");
        Assert.That(await FlagUpdatedAtAsync(flagId), Is.EqualTo(before));

        var queue = await _oversight.GetQueueAsync(new OversightQuery(Guid.NewGuid()), Both);
        Assert.That(queue.Cases.Select(c => c.Id), Does.Not.Contain(caseId), "A closed case leaves the open queue.");
        Assert.That((await _oversight.GetQueueAsync(new OversightQuery(Guid.NewGuid(), OversightView.All), Both))
            .Cases.Select(c => c.Id), Does.Contain(caseId), "…but stays on the record.");
    }

    [Test]
    public async Task Reopening_clears_the_verdict_and_keeps_the_history()
    {
        var mismatch = await SeedMismatchAsync("C-06");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await _oversight.ResolveAsync(caseId, OversightResolution.NoActionNeeded, "Místo bylo mezitím uvolněné.", first, Both);
        Assert.That((await _oversight.ResolveAsync(caseId, OversightResolution.Founded, null, second, Both)).Errors,
            Does.Contain("Parking_Oversight_Error_AlreadyResolved"));

        var reopened = await _oversight.ReopenAsync(caseId, "Přišla fotka od ostrahy.", second, Both);
        Assert.That(reopened.Succeeded, Is.True, reopened.Errors.FirstOrDefault());

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(detail!.Status, Is.EqualTo(OversightCaseStatus.InProgress));
        Assert.That(detail.Resolution, Is.Null, "A reopened case has no verdict standing.");
        Assert.That(detail.ResolutionNote, Is.Null);
        Assert.That(detail.AssigneeId, Is.EqualTo(second), "Whoever reopened it is holding it.");
        Assert.That(detail.Timeline.Select(e => e.Type),
            Does.Contain(OversightEventType.Resolved).And.Contain(OversightEventType.Reopened),
            "The superseded ruling stays in the history — that is where a withdrawn verdict belongs.");
    }

    [Test]
    public async Task Priority_is_recorded_with_what_it_changed_from()
    {
        var mismatch = await SeedMismatchAsync("C-07");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);
        var reviewer = Guid.NewGuid();

        await _oversight.SetPriorityAsync(caseId, OversightCasePriority.High, reviewer, Both);
        // Setting the priority it already has is a no-op, not a second line in the history.
        await _oversight.SetPriorityAsync(caseId, OversightCasePriority.High, reviewer, Both);

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        var change = detail!.Timeline.Single(e => e.Type == OversightEventType.PriorityChanged);

        Assert.That(detail.Priority, Is.EqualTo(OversightCasePriority.High));
        Assert.That(change.Body, Is.EqualTo("Normal → High"));
    }

    // --- seeding ---------------------------------------------------------------------------

    private async Task<OccupancyMismatch> SeedMismatchAsync(string spotCode)
    {
        var spot = new ParkingSpot(spotCode, ParkingSpotType.Standard);
        var reporter = await SeedUserAsync();
        var mismatch = new OccupancyMismatch(
            spot.Id, Guid.NewGuid(), reporter, Now.AddHours(-3), Now.AddHours(-1), Now.AddHours(-2));
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.OccupancyMismatches.Add(mismatch);
        });
        return mismatch;
    }

    private async Task<Guid> SeedFlagAsync()
    {
        var flag = new CollusionFlag(Guid.NewGuid(), Guid.NewGuid(), 6, 90, 85, Now.AddDays(-1));
        await SeedAsync(db => db.CollusionFlags.Add(flag));
        return flag.Id;
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = new ApplicationUser
        {
            UserName = $"reviewer-{Guid.NewGuid():N}",
            Email = $"reviewer-{Guid.NewGuid():N}@test.local",
            DisplayName = "Zkušební správce",
        };
        await SeedAsync(db => db.Users.Add(user));
        return user.Id;
    }

    /// <summary>A reserved spot whose window covers <see cref="Now"/>, so the report is in its legal window.</summary>
    private async Task<(Guid UserId, Reservation Reservation)> SeedBlockedReservationAsync(string spotCode)
    {
        var userId = await SeedUserAsync();
        var spot = new ParkingSpot(spotCode, ParkingSpotType.Standard);
        var reservation = new Reservation(spot.Id, userId, Now.AddHours(-1), Now.AddHours(2), false, Now.AddHours(-2), creditsCharged: 0);
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.ParkerScores.Add(new ParkerScore(userId));
            db.Reservations.Add(reservation);
        });
        return (userId, reservation);
    }

    /// <summary>Completed reservations of one guest on one resident's spot — the edge the scan measures.</summary>
    private async Task<(Guid Owner, Guid Guest)> SeedSharingPairAsync(string spotCode, int interactions)
    {
        var owner = await SeedUserAsync();
        var guest = await SeedUserAsync();
        var spot = new ParkingSpot(spotCode, ParkingSpotType.Standard);
        spot.AssignOwner(owner);
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            for (var i = 0; i < interactions; i++)
            {
                var reservation = new Reservation(
                    spot.Id, guest, Now.AddDays(-i - 2), Now.AddDays(-i - 2).AddHours(8), false, Now.AddDays(-i - 3), creditsCharged: 0);
                reservation.CheckIn(Now.AddDays(-i - 2));
                reservation.Complete(Now.AddDays(-i - 2).AddHours(8));
                db.Reservations.Add(reservation);
            }
        });
        return (owner, guest);
    }

    private async Task SeedSettingsAsync()
    {
        await using var db = new D3ParkingDbContext(_options);
        if (!await db.ParkingSettings.AnyAsync(s => s.Id == ParkingSettings.SingletonId))
        {
            db.ParkingSettings.Add(ParkingSettings.CreateDefault());
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Lets the next scan run immediately instead of waiting out the configured interval.</summary>
    private async Task ClearScanTimestampAsync()
    {
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("UPDATE ParkingSettings SET LastCollusionScanUtc = NULL");
    }

    private async Task<Guid> CaseIdForAsync(OversightCaseKind kind, Guid subjectId)
    {
        await using var db = new D3ParkingDbContext(_options);
        return await db.OversightCases
            .Where(c => c.Kind == kind && c.SubjectId == subjectId)
            .Select(c => c.Id)
            .SingleAsync();
    }

    private async Task<Guid> MismatchIdOfAsync(Guid reporterId)
    {
        await using var db = new D3ParkingDbContext(_options);
        return await db.OccupancyMismatches.Where(m => m.ReporterId == reporterId).Select(m => m.Id).SingleAsync();
    }

    private async Task<Guid> FlagIdOfAsync(Guid a, Guid b)
    {
        var (first, second) = CollusionFlag.Key(a, b);
        await using var db = new D3ParkingDbContext(_options);
        return await db.CollusionFlags.Where(f => f.UserA == first && f.UserB == second).Select(f => f.Id).SingleAsync();
    }

    private async Task<DateTimeOffset> FlagUpdatedAtAsync(Guid flagId)
    {
        await using var db = new D3ParkingDbContext(_options);
        return await db.CollusionFlags.Where(f => f.Id == flagId).Select(f => f.UpdatedAtUtc).SingleAsync();
    }

    private async Task SeedAsync(Action<D3ParkingDbContext> seed)
    {
        await using var db = new D3ParkingDbContext(_options);
        seed(db);
        await db.SaveChangesAsync();
    }

    private static BlockedSpotPhoto Photo(byte seed)
    {
        var content = new byte[256];
        content[0] = seed;
        for (var i = 1; i < content.Length; i++)
        {
            content[i] = (byte)(seed * 37 + i);
        }

        return new BlockedSpotPhoto(content, "image/jpeg");
    }
}
