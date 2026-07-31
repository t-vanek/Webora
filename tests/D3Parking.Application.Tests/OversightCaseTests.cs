using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Oversight;
using D3Parking.Application.Parking;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
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

    /// <summary>Sees every kind, but may not rule against anybody — what most reviewers hold.</summary>
    private static readonly OversightScope Both = OversightScope.From(true, true, manageSpots: true);

    /// <summary>Sees reports only — the split that keeps photographs of third parties' cars in one queue.</summary>
    private static readonly OversightScope MismatchesOnly = OversightScope.From(true, false);

    /// <summary>Maintains the lot and nothing else: sees defects, no evidence about anybody.</summary>
    private static readonly OversightScope SpotsOnly = OversightScope.From(false, false, manageSpots: true);

    /// <summary>Sees everything and may rule against a person.</summary>
    private static readonly OversightScope Sanctioning =
        OversightScope.From(true, true, manageSpots: true, maySanction: true);

    /// <summary>Sees everything and may put a case on somebody else's desk.</summary>
    private static readonly OversightScope Assigning =
        OversightScope.From(true, true, manageSpots: true, mayAssign: true);

    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private OversightService _oversight = null!;
    private ReservationService _reservations = null!;
    private CollusionService _collusion = null!;
    private RecordingNotificationService _notifications = null!;
    private AdvancingTimeProvider _clock = null!;

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
        // Advanceable rather than fixed: deadlines are the point of half of these specs, and a
        // clock that cannot move cannot show one passing.
        _clock = new AdvancingTimeProvider(Now);
        var notifications = new RecordingNotificationService();
        var messages = new PassthroughLocalizer<ParkingMessages>();
        var siteSettings = new FakeSiteSettings();

        _notifications = notifications;
        var spots = new ParkingSpotService(factory, notifications, siteSettings, _clock, messages);
        _collusion = new CollusionService(factory, _clock);
        _reservations = new ReservationService(
            factory, new FakeParkingSettings(IncentivePolicy.Default), siteSettings, _clock, notifications, messages);
        _oversight = new OversightService(factory, spots, _collusion, siteSettings, notifications, messages, _clock);
    }

    [SetUp]
    public void ResetClockAndNotifications()
    {
        _clock.UtcNow = Now;
        _notifications.Sent.Clear();
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

    // --- deadlines, triage and the lot's own turn at the desk --------------------------------

    [Test]
    public async Task A_case_opens_with_the_deadline_its_priority_earns()
    {
        await SeedSettingsAsync();
        var mismatch = await SeedMismatchAsync("C-10");
        await _oversight.EnsureCasesAsync();

        var detail = await _oversight.GetCaseAsync(
            await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id), Both);

        Assert.That(detail!.Priority, Is.EqualTo(OversightCasePriority.Normal));
        Assert.That(detail.DueAtUtc, Is.EqualTo(mismatch.ReportedAtUtc.AddHours(72)),
            "The clock starts when the signal was raised, not when the ingest noticed it.");
        Assert.That(detail.IsOverdue, Is.False);
    }

    [Test]
    public async Task Repeated_reports_on_one_spot_open_high_and_say_so_on_the_ones_already_open()
    {
        await SeedSettingsAsync();
        var spot = await SeedSpotAsync("C-11");
        var first = await SeedMismatchOnAsync(spot, Now.AddDays(-3));
        var second = await SeedMismatchOnAsync(spot, Now.AddDays(-2));
        await _oversight.EnsureCasesAsync();

        // The third inside the window is what turns three incidents into a pattern.
        var third = await SeedMismatchOnAsync(spot, Now.AddHours(-1));
        await _oversight.EnsureCasesAsync();

        var latest = await _oversight.GetCaseAsync(
            await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, third.Id), Both);
        Assert.That(latest!.Priority, Is.EqualTo(OversightCasePriority.High));
        Assert.That(latest.DueAtUtc, Is.EqualTo(third.ReportedAtUtc.AddHours(24)),
            "A higher priority is a shorter deadline; that is what raising it is for.");
        Assert.That(latest.Timeline.Select(e => e.Type), Does.Contain(OversightEventType.Escalated));

        foreach (var earlier in new[] { first, second })
        {
            var open = await _oversight.GetCaseAsync(
                await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, earlier.Id), Both);
            Assert.That(open!.Timeline.Select(e => e.Type), Does.Contain(OversightEventType.SignalUpdated),
                "Whoever is holding an open case on this spot has to learn it happened again.");
        }
    }

    [Test]
    public async Task A_passed_deadline_is_announced_once()
    {
        await SeedSettingsAsync();
        var reviewer = await SeedReviewerAsync(Permissions.Parking.ReviewMismatches);
        var mismatch = await SeedMismatchAsync("C-12");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);

        await _oversight.RunDueCaseWorkAsync();
        Assert.That((await _oversight.GetCaseAsync(caseId, Both))!.IsOverdue, Is.False, "Nothing is overdue yet.");

        _clock.UtcNow = Now.AddDays(5);
        _notifications.Sent.Clear();
        await _oversight.RunDueCaseWorkAsync();

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(detail!.IsOverdue, Is.True);
        Assert.That(detail.Timeline.Count(e => e.Type == OversightEventType.SlaBreached), Is.EqualTo(1));
        Assert.That(_notifications.Sent, Does.Contain((reviewer, "Parking_Notify_OversightOverdue_Title")),
            "An unclaimed case is everyone's to pick up, so everyone who could is told.");

        // A sweep runs every few minutes; the breach must not be re-announced on each one. The
        // count is of this case's own timeline — the fixture shares a database, so a global tally
        // would be measuring the other specs' cases too.
        await _oversight.RunDueCaseWorkAsync();
        var again = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(again!.Timeline.Count(e => e.Type == OversightEventType.SlaBreached), Is.EqualTo(1));
    }

    [Test]
    public async Task Raising_the_priority_re_dates_the_deadline_from_now()
    {
        await SeedSettingsAsync();
        var mismatch = await SeedMismatchAsync("C-13");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);

        // Let it go overdue first, so the re-dating has something to clear.
        _clock.UtcNow = Now.AddDays(5);
        await _oversight.RunDueCaseWorkAsync();
        Assert.That((await _oversight.GetCaseAsync(caseId, Both))!.IsOverdue, Is.True);

        var raised = await _oversight.SetPriorityAsync(caseId, OversightCasePriority.Critical, Guid.NewGuid(), Both);
        Assert.That(raised.Succeeded, Is.True, raised.Errors.FirstOrDefault());

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(detail!.DueAtUtc, Is.EqualTo(_clock.UtcNow.AddHours(4)),
            "Raising an old case to critical means deal with it now, not four hours ago.");
        Assert.That(detail.IsOverdue, Is.False);

        // And it can breach again on the new terms.
        _clock.UtcNow = _clock.UtcNow.AddHours(5);
        await _oversight.RunDueCaseWorkAsync();
        var breached = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(breached!.IsOverdue, Is.True);
        Assert.That(breached.Timeline.Count(e => e.Type == OversightEventType.SlaBreached), Is.EqualTo(2),
            "The second deadline is a second breach, not a repeat of the first.");
    }

    [Test]
    public async Task A_rescan_of_a_flagged_pair_lands_on_its_timeline_once()
    {
        await SeedSettingsAsync();
        var flagId = await SeedFlagAsync();
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.CollusionRing, flagId);

        await using (var db = new D3ParkingDbContext(_options))
        {
            var flag = await db.CollusionFlags.SingleAsync(f => f.Id == flagId);
            flag.Update(11, 95, 93, Now.AddHours(2));
            await db.SaveChangesAsync();
        }

        await _oversight.RunDueCaseWorkAsync();
        var detail = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(detail!.Timeline.Count(e => e.Type == OversightEventType.SignalUpdated), Is.EqualTo(1),
            "The numbers on the evidence panel changed under the reviewer; the timeline has to say so.");

        // Nothing moved since, so nothing more to say.
        await _oversight.RunDueCaseWorkAsync();
        var again = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(again!.Timeline.Count(e => e.Type == OversightEventType.SignalUpdated), Is.EqualTo(1));
    }

    [Test]
    public async Task An_urgent_case_reaches_only_the_reviewers_of_its_kind()
    {
        await SeedSettingsAsync();
        var mismatchReviewer = await SeedReviewerAsync(Permissions.Parking.ReviewMismatches);
        var collusionReviewer = await SeedReviewerAsync(Permissions.Parking.ReviewCollusion);
        var spot = await SeedSpotAsync("C-14");
        await SeedMismatchOnAsync(spot, Now.AddDays(-2));
        await SeedMismatchOnAsync(spot, Now.AddDays(-1));
        await SeedMismatchOnAsync(spot, Now.AddHours(-1));

        await _oversight.EnsureCasesAsync();

        var urgent = _notifications.Sent.Where(s => s.Title == "Parking_Notify_OversightNew_Title").ToList();
        Assert.That(urgent.Select(s => s.UserId), Does.Not.Contain(collusionReviewer),
            "The evidence is a photograph of someone's car; only the permission that may see it hears about it.");
        Assert.That(urgent.Count(s => s.UserId == mismatchReviewer), Is.EqualTo(1),
            "Three reports, one of them escalated: only that one is worth interrupting anyone for — "
            + "the other two wait for the digest.");
    }

    [Test]
    public async Task The_daily_digest_is_one_message_per_reviewer_per_day()
    {
        await SeedSettingsAsync();
        // Holds both permissions, so both queues are one pile of work to them.
        var reviewer = await SeedReviewerAsync(Permissions.Parking.ReviewMismatches, Permissions.Parking.ReviewCollusion);
        await SeedMismatchAsync("C-15");
        await SeedFlagAsync();
        await _oversight.EnsureCasesAsync();

        await ClearDigestMarkerAsync();
        // Built with an explicit zero offset: the site time zone here is UTC, and going through
        // DateTimeOffset.Date would hand back a DateTime that picks up the machine's offset.
        _clock.UtcNow = new DateTimeOffset(2026, 7, 16, 9, 0, 0, TimeSpan.Zero);
        _notifications.Sent.Clear();
        await _oversight.RunDueCaseWorkAsync();

        Assert.That(_notifications.Sent.Count(s => s.UserId == reviewer && s.Title == "Parking_Notify_OversightDigest_Title"),
            Is.EqualTo(1),
            "Two kinds of case are still one pile of work; telling them twice is the noise the digest exists to end.");

        // Later the same day the sweep runs again — and stays quiet.
        _clock.UtcNow = _clock.UtcNow.AddHours(6);
        _notifications.Sent.Clear();
        await _oversight.RunDueCaseWorkAsync();
        Assert.That(_notifications.Sent.Where(s => s.Title == "Parking_Notify_OversightDigest_Title"), Is.Empty);
    }

    // --- the driver's side ------------------------------------------------------------------

    [Test]
    public async Task Waiting_on_the_driver_stops_the_clock_and_answering_starts_it_again()
    {
        await SeedSettingsAsync();
        var mismatch = await SeedMismatchAsync("C-20");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);
        var reviewer = Guid.NewGuid();
        await _oversight.ClaimAsync(caseId, reviewer, Both);

        var due = (await _oversight.GetCaseAsync(caseId, Both))!.DueAtUtc;
        var asked = await _oversight.RequestInfoAsync(caseId, "Poznal(a) jste značku vozu?", reviewer, Both);
        Assert.That(asked.Succeeded, Is.True, asked.Errors.FirstOrDefault());

        // Two days of waiting must not eat the reviewer's deadline...
        _clock.UtcNow = Now.AddDays(2);
        await _oversight.RunDueCaseWorkAsync();
        var waiting = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(waiting!.Status, Is.EqualTo(OversightCaseStatus.AwaitingInfo));
        Assert.That(waiting.IsOverdue, Is.False, "The case is not the reviewer's move while it waits.");
        Assert.That(waiting.DueAtUtc, Is.EqualTo(due), "The deadline holds still until the answer lands.");

        var answered = await _oversight.AddParticipantNoteAsync(caseId, "Ano, 3AB 1234.", mismatch.ReporterId);
        Assert.That(answered.Succeeded, Is.True, answered.Errors.FirstOrDefault());

        // ...and once it lands, the reviewer gets that time back.
        var resumed = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(resumed!.Status, Is.EqualTo(OversightCaseStatus.InProgress));
        Assert.That(resumed.DueAtUtc, Is.EqualTo(due!.Value.AddDays(2)));
        Assert.That(resumed.Timeline.Select(e => e.Type),
            Does.Contain(OversightEventType.InfoRequested).And.Contain(OversightEventType.InfoProvided));
    }

    [Test]
    public async Task A_question_nobody_answers_puts_the_case_back_on_the_desk()
    {
        await SeedSettingsAsync();
        var mismatch = await SeedMismatchAsync("C-21");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);
        var reviewer = Guid.NewGuid();
        await _oversight.ClaimAsync(caseId, reviewer, Both);
        await _oversight.RequestInfoAsync(caseId, "Máte fotku i z druhé strany?", reviewer, Both);

        _clock.UtcNow = Now.AddDays(8);
        await _oversight.RunDueCaseWorkAsync();

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(detail!.Status, Is.EqualTo(OversightCaseStatus.InProgress),
            "The lot can tell nobody replied; what that means for the report is still a person's call.");
        Assert.That(detail.Resolution, Is.Null, "Silence is not a verdict.");
        Assert.That(detail.Timeline.Select(e => e.Type), Does.Contain(OversightEventType.SignalUpdated));
    }

    [Test]
    public async Task A_driver_sees_their_own_reports_and_only_what_faces_them()
    {
        await SeedSettingsAsync();
        var mine = await SeedMismatchAsync("C-22");
        var someoneElses = await SeedMismatchAsync("C-23");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mine.Id);
        var reviewer = Guid.NewGuid();

        await _oversight.CommentAsync(caseId, "Fotka je rozmazaná, ověřit u ostrahy.", visibleToParticipants: false, reviewer, Both);
        await _oversight.CommentAsync(caseId, "Prověřujeme, ozveme se.", visibleToParticipants: true, reviewer, Both);

        var reports = await _oversight.GetMyReportsAsync(mine.ReporterId);

        Assert.That(reports.Select(r => r.Id), Is.EquivalentTo(new[] { caseId }),
            "Somebody else's report is nobody's business, whatever its number.");
        var bodies = reports[0].Timeline.Select(e => e.Body).ToList();
        Assert.That(bodies, Does.Contain("Prověřujeme, ozveme se."));
        Assert.That(bodies, Does.Not.Contain("Fotka je rozmazaná, ověřit u ostrahy."),
            "Internal notes never leave the database, so no template can leak one.");
        Assert.That(reports[0].Timeline.Select(e => e.ActorName), Has.All.Null,
            "The driver is owed the decision and its reasons, not a colleague to corner about it.");

        // And a stranger cannot write to it either.
        var intruder = await _oversight.AddParticipantNoteAsync(caseId, "pustte me dovnitr", someoneElses.ReporterId);
        Assert.That(intruder.Succeeded, Is.False);
        Assert.That(intruder.Errors, Does.Contain("Parking_Oversight_Error_NotFound"));
    }

    [Test]
    public async Task A_rejected_apology_can_be_disputed_once()
    {
        await SeedSettingsAsync();
        var reviewer = await SeedUserAsync();
        var (driver, reservation) = await SeedBlockedReservationAsync("C-24");
        var report = await _reservations.ReportBlockedSpotAsync(driver, reservation.Id, relocate: false, Photo(24));
        Assert.That(report.VoucherGranted, Is.True, report.Error);

        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, await MismatchIdOfAsync(driver));
        await _oversight.ReviewVoucherAsync(caseId, approve: false, reviewer, Both);

        var mine = (await _oversight.GetMyReportsAsync(driver)).Single(r => r.Id == caseId);
        Assert.That(mine.Status, Is.EqualTo(OversightCaseStatus.Resolved));
        Assert.That(mine.CanAppeal, Is.True, "A ruling that cost the driver something has to be answerable.");

        var appeal = await _oversight.AppealAsync(caseId, "Auto tam stálo, mám i video.", driver);
        Assert.That(appeal.Succeeded, Is.True, appeal.Errors.FirstOrDefault());

        var reopened = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(reopened!.Status, Is.EqualTo(OversightCaseStatus.New));
        Assert.That(reopened.AssigneeId, Is.Null,
            "An appeal must not land back with the reviewer whose ruling is being disputed.");
        Assert.That(reopened.Resolution, Is.Null);
        Assert.That(reopened.Timeline.Select(e => e.Type), Does.Contain(OversightEventType.Appealed));

        // Answerable once — not arguable in circles.
        await _oversight.ResolveAsync(caseId, OversightResolution.Unfounded, "Video nic nedokládá.", reviewer, Both);
        var again = await _oversight.AppealAsync(caseId, "Pořád nesouhlasím.", driver);
        Assert.That(again.Succeeded, Is.False);
        Assert.That(again.Errors, Does.Contain("Parking_Oversight_Error_AlreadyAppealed"));
        Assert.That((await _oversight.GetMyReportsAsync(driver)).Single(r => r.Id == caseId).CanAppeal, Is.False);
    }

    [Test]
    public async Task A_flagged_pair_has_nobody_to_ask()
    {
        await SeedSettingsAsync();
        var flagId = await SeedFlagAsync();
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.CollusionRing, flagId);
        var reviewer = Guid.NewGuid();
        await _oversight.ClaimAsync(caseId, reviewer, Both);

        var asked = await _oversight.RequestInfoAsync(caseId, "Vysvětlíte to?", reviewer, Both);

        Assert.That(asked.Succeeded, Is.False,
            "Telling two colleagues they are suspected is not a question, and this is not the place to decide it.");
        Assert.That(asked.Errors, Does.Contain("Parking_Oversight_Error_NoParticipant"));
    }

    // --- defects and rulings ----------------------------------------------------------------

    [Test]
    public async Task Anybody_can_report_something_wrong_with_the_lot()
    {
        await SeedSettingsAsync();
        var spot = await SeedSpotAsync("C-30");
        var reporter = await SeedUserAsync();

        var sent = await _oversight.ReportDefectAsync(
            reporter, SpotDefectCategory.Lighting, "  Nesvítí lampa nad místem.  ", spot);
        Assert.That(sent.Succeeded, Is.True, sent.Errors.FirstOrDefault());

        // The report is on the desk immediately — the reporter does not wait for a sweep.
        var mine = (await _oversight.GetMyReportsAsync(reporter)).Single();
        Assert.That(mine.Kind, Is.EqualTo(OversightCaseKind.SpotDefect));
        Assert.That(mine.SpotCode, Is.EqualTo("C-30"));
        Assert.That(mine.VoucherStatus, Is.Null, "Nothing is owed for pointing out a broken light.");

        var detail = await _oversight.GetCaseAsync(mine.Id, Both);
        Assert.That(detail!.Defect!.Description, Is.EqualTo("Nesvítí lampa nad místem."));
        Assert.That(detail.Defect.Category, Is.EqualTo(SpotDefectCategory.Lighting));
        Assert.That(detail.Parties, Is.Empty,
            "Somebody who says a light is out is not a party to anything.");

        Assert.That((await _oversight.ReportDefectAsync(reporter, SpotDefectCategory.Other, "   ", null)).Errors,
            Does.Contain("Parking_Defect_Error_DescriptionRequired"));
    }

    [Test]
    public async Task Defects_are_seen_by_the_permission_that_maintains_the_lot()
    {
        await SeedSettingsAsync();
        var reporter = await SeedUserAsync();
        await _oversight.ReportDefectAsync(reporter, SpotDefectCategory.Access, "Závora nečte karty.", null);
        var caseId = (await _oversight.GetMyReportsAsync(reporter)).Single().Id;

        // The review permissions guard evidence about people; a broken barrier is not that.
        Assert.That(await _oversight.GetCaseAsync(caseId, MismatchesOnly), Is.Null);
        Assert.That(await _oversight.GetCaseAsync(caseId, SpotsOnly), Is.Not.Null);

        var queue = await _oversight.GetQueueAsync(new OversightQuery(Guid.NewGuid()), SpotsOnly);
        Assert.That(queue.Cases.Select(c => c.Kind), Has.All.EqualTo(OversightCaseKind.SpotDefect));
        Assert.That(queue.Cases.Single(c => c.Id == caseId).Subject, Is.EqualTo("Závora nečte karty."),
            "A defect about the lot itself has no spot to name it by, so it borrows its own words.");
    }

    [Test]
    public async Task A_ruling_against_a_person_needs_the_permission_a_party_and_a_reason()
    {
        await SeedSettingsAsync();
        var reviewer = await SeedUserAsync();
        var stranger = await SeedUserAsync();
        var mismatch = await SeedMismatchAsync("C-31");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);

        var withoutPermission = await _oversight.SanctionAsync(caseId, mismatch.ReporterId, 10, 0, "Vymyšlené hlášení.", reviewer, Both);
        Assert.That(withoutPermission.Errors, Does.Contain("Parking_Oversight_Error_MayNotSanction"));

        var withoutReason = await _oversight.SanctionAsync(caseId, mismatch.ReporterId, 10, 0, " ", reviewer, Sanctioning);
        Assert.That(withoutReason.Errors, Does.Contain("Parking_Oversight_Error_ReasonRequired"));

        var atAStranger = await _oversight.SanctionAsync(caseId, stranger, 10, 0, "Vymyšlené hlášení.", reviewer, Sanctioning);
        Assert.That(atAStranger.Errors, Does.Contain("Parking_Oversight_Error_NotAParty"),
            "A ruling is aimed at a party to the thing being decided, not at whoever is typed in.");
    }

    [Test]
    public async Task A_ruling_lands_on_the_case_the_ledger_the_audit_trail_and_the_person()
    {
        await SeedSettingsAsync();
        var reviewer = await SeedUserAsync();
        var mismatch = await SeedMismatchAsync("C-32");
        await SeedScoreAsync(mismatch.ReporterId, points: 40, credits: 25);
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);

        var ruled = await _oversight.SanctionAsync(caseId, mismatch.ReporterId, 15, 5, "Hlášení bylo vymyšlené.", reviewer, Sanctioning);
        Assert.That(ruled.Succeeded, Is.True, ruled.Errors.FirstOrDefault());

        await using var db = new D3ParkingDbContext(_options);
        var score = await db.ParkerScores.SingleAsync(s => s.UserId == mismatch.ReporterId);
        Assert.That(score.Points, Is.EqualTo(25));
        Assert.That(score.Credits, Is.EqualTo(20));
        Assert.That(score.NoShows, Is.Zero, "Nobody failed to turn up; the behaviour counters must not move.");

        var ledger = await db.PointsLedgerEntries.Where(e => e.UserId == mismatch.ReporterId).ToListAsync();
        Assert.That(ledger.Select(e => e.Points), Is.EquivalentTo(new[] { -15, -5 }));
        Assert.That(ledger.Select(e => e.Reason), Has.All.EqualTo(IncentiveReason.ManualAdjustment));

        Assert.That(await db.AccountAuditEvents.AnyAsync(e =>
            e.UserId == mismatch.ReporterId && e.Type == AccountAuditEventType.OversightSanctioned), Is.True,
            "An oversight case is not the first place anyone looks when asking what happened to an account.");

        Assert.That(_notifications.Sent, Does.Contain((mismatch.ReporterId, "Parking_Notify_OversightSanction_Title")));

        // And the driver sees the ruling and its reason on their own page.
        var mine = (await _oversight.GetMyReportsAsync(mismatch.ReporterId)).Single(r => r.Id == caseId);
        var entry = mine.Timeline.Single(e => e.Type == OversightEventType.Sanctioned);
        Assert.That(entry.Body, Does.Contain("Hlášení bylo vymyšlené."));
    }

    [Test]
    public async Task A_case_can_be_handed_to_a_colleague_who_can_actually_see_it()
    {
        await SeedSettingsAsync();
        var mismatchReviewer = await SeedReviewerAsync(Permissions.Parking.ReviewMismatches);
        var collusionReviewer = await SeedReviewerAsync(Permissions.Parking.ReviewCollusion);
        var mismatch = await SeedMismatchAsync("C-40");
        await _oversight.EnsureCasesAsync();
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatch.Id);
        var actor = Guid.NewGuid();

        var withoutPermission = await _oversight.AssignAsync(caseId, mismatchReviewer, actor, Both);
        Assert.That(withoutPermission.Errors, Does.Contain("Parking_Oversight_Error_MayNotAssign"),
            "Taking a case is anyone's; putting one on somebody else's desk is not.");

        var toTheWrongQueue = await _oversight.AssignAsync(caseId, collusionReviewer, actor, Assigning);
        Assert.That(toTheWrongQueue.Errors, Does.Contain("Parking_Oversight_Error_NotAReviewer"),
            "Handing a case to somebody who cannot open it is a dead end that looks like progress.");

        _notifications.Sent.Clear();
        var handed = await _oversight.AssignAsync(caseId, mismatchReviewer, actor, Assigning);
        Assert.That(handed.Succeeded, Is.True, handed.Errors.FirstOrDefault());

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(detail!.AssigneeId, Is.EqualTo(mismatchReviewer));
        Assert.That(detail.Status, Is.EqualTo(OversightCaseStatus.InProgress));
        Assert.That(detail.Timeline.Single(e => e.Type == OversightEventType.Assigned).Body,
            Is.EqualTo("Kolega z dohledu"),
            "The name is recorded as it read then — a later rename must not rewrite the history.");
        Assert.That(_notifications.Sent, Does.Contain((mismatchReviewer, "Parking_Notify_OversightAssigned_Title")));

        var candidates = await _oversight.GetAssignableReviewersAsync(OversightCaseKind.OccupancyMismatch);
        Assert.That(candidates.Select(c => c.UserId), Does.Contain(mismatchReviewer));
        Assert.That(candidates.Select(c => c.UserId), Does.Not.Contain(collusionReviewer));
    }

    [Test]
    public async Task A_driver_can_take_their_own_report_back_and_gives_up_the_apology_with_it()
    {
        await SeedSettingsAsync();
        var (driver, reservation) = await SeedBlockedReservationAsync("C-50");
        var report = await _reservations.ReportBlockedSpotAsync(driver, reservation.Id, relocate: false, Photo(50));
        Assert.That(report.VoucherGranted, Is.True, report.Error);

        await _oversight.EnsureCasesAsync();
        var mismatchId = await MismatchIdOfAsync(driver);
        var caseId = await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, mismatchId);

        Assert.That((await _oversight.WithdrawAsync(caseId, "  ", driver)).Errors,
            Does.Contain("Parking_Oversight_Error_ReasonRequired"));

        var withdrawn = await _oversight.WithdrawAsync(caseId, "Spletl jsem si řadu, místo bylo volné.", driver);
        Assert.That(withdrawn.Succeeded, Is.True, withdrawn.Errors.FirstOrDefault());

        var detail = await _oversight.GetCaseAsync(caseId, Both);
        Assert.That(detail!.Status, Is.EqualTo(OversightCaseStatus.Resolved));
        Assert.That(detail.Resolution, Is.EqualTo(OversightResolution.Unfounded));
        Assert.That(detail.AssigneeId, Is.Null,
            "The driver ended the case; they did not work it, and must not read as its reviewer.");
        Assert.That(detail.Timeline.Select(e => e.Type), Does.Contain(OversightEventType.Withdrawn));

        await using var db = new D3ParkingDbContext(_options);
        var voucher = await db.ApologyVouchers.SingleAsync(v => v.SourceMismatchId == mismatchId);
        Assert.That(voucher.Status, Is.EqualTo(ApologyVoucherStatus.Rejected),
            "The apology goes with the report — the one move on their own voucher that only costs them.");
        Assert.That(voucher.ReviewedById, Is.Null, "Nobody ruled on it, so no reviewer is recorded.");
        Assert.That(await _reservations.GetMyApologyVoucherAsync(driver), Is.Null);
    }

    [Test]
    public async Task A_plate_nobody_owns_opens_the_case_urgently()
    {
        await SeedSettingsAsync();
        var spot = await SeedSpotAsync("C-51");
        var reporter = await SeedUserAsync();

        // Reported for a window happening today: a car in the way right now, not a record.
        var outsider = new OccupancyMismatch(
            spot, Guid.NewGuid(), reporter, Now.AddHours(-2), Now.AddHours(1), Now.AddHours(-1), "9XY 4321");
        await SeedAsync(db => db.OccupancyMismatches.Add(outsider));
        await _oversight.EnsureCasesAsync();

        var detail = await _oversight.GetCaseAsync(
            await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, outsider.Id), Both);
        Assert.That(detail!.Priority, Is.EqualTo(OversightCasePriority.Critical));
        Assert.That(detail.Timeline.Select(e => e.Type), Does.Contain(OversightEventType.Escalated));
    }

    [Test]
    public async Task A_plate_the_lot_knows_is_not_an_outsider()
    {
        await SeedSettingsAsync();
        var spot = await SeedSpotAsync("C-52");
        var reporter = await SeedUserAsync();

        // Same plate, typed with different spacing and case — the one canonical form must match.
        await SeedAsync(db => db.Users.Add(new ApplicationUser
        {
            UserName = $"owner-{Guid.NewGuid():N}",
            Email = $"owner-{Guid.NewGuid():N}@test.local",
            LicensePlate = "5ab 6789",
        }));

        var known = new OccupancyMismatch(
            spot, Guid.NewGuid(), reporter, Now.AddHours(-2), Now.AddHours(1), Now.AddHours(-1), "5AB-6789");
        await SeedAsync(db => db.OccupancyMismatches.Add(known));
        await _oversight.EnsureCasesAsync();

        var detail = await _oversight.GetCaseAsync(
            await CaseIdForAsync(OversightCaseKind.OccupancyMismatch, known.Id), Both);
        Assert.That(detail!.Priority, Is.EqualTo(OversightCasePriority.Normal),
            "A colleague's car is a conversation, not an emergency.");
    }

    [Test]
    public async Task A_defect_can_carry_a_picture_but_does_not_have_to()
    {
        await SeedSettingsAsync();
        var reporter = await SeedUserAsync();
        var spot = await SeedSpotAsync("C-53");

        await _oversight.ReportDefectAsync(reporter, SpotDefectCategory.Surface, "Díra u sloupu.", spot, Photo(53));
        await _oversight.ReportDefectAsync(reporter, SpotDefectCategory.Lighting, "Nesvítí lampa.", spot);

        var reports = await _oversight.GetMyReportsAsync(reporter);
        var withPhoto = await DefectDetailAsync(reports, "Díra u sloupu.");
        var without = await DefectDetailAsync(reports, "Nesvítí lampa.");

        Assert.That(withPhoto.HasPhoto, Is.True);
        Assert.That(without.HasPhoto, Is.False, "A defect nobody photographed is still worth knowing about.");
        Assert.That((await _oversight.GetDefectPhotoAsync(withPhoto.Id))!.Content, Is.EqualTo(Photo(53).Content));
        Assert.That(await _oversight.GetDefectPhotoAsync(without.Id), Is.Null);

        // The same picture may accompany a second report — it proves nothing, so there is nothing
        // for a fingerprint to defend against.
        var again = await _oversight.ReportDefectAsync(reporter, SpotDefectCategory.Surface, "Ta díra je pořád tady.", spot, Photo(53));
        Assert.That(again.Succeeded, Is.True, again.Errors.FirstOrDefault());
    }

    private async Task<SpotDefectDto> DefectDetailAsync(IReadOnlyList<MyOversightReportDto> reports, string description)
    {
        foreach (var report in reports.Where(r => r.Kind == OversightCaseKind.SpotDefect))
        {
            var detail = await _oversight.GetCaseAsync(report.Id, Both);
            if (detail?.Defect is { } defect && defect.Description == description)
            {
                return defect;
            }
        }

        throw new InvalidOperationException($"No defect case for '{description}'.");
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

    /// <summary>A standing to deduct from, so a ruling has something to bite on.</summary>
    private async Task SeedScoreAsync(Guid userId, int points, int credits)
    {
        await using var db = new D3ParkingDbContext(_options);
        var score = new ParkerScore(userId);
        score.RewardStreak(points, Now);
        db.ParkerScores.Add(score);
        await db.SaveChangesAsync();

        // RewardStreak tops up both sides equally; nudge the wallet to the asked-for figure.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE ParkerScores SET Credits = {0} WHERE UserId = {1}", credits, userId);
    }

    private async Task<Guid> SeedSpotAsync(string spotCode)
    {
        var spot = new ParkingSpot(spotCode, ParkingSpotType.Standard);
        await SeedAsync(db => db.ParkingSpots.Add(spot));
        return spot.Id;
    }

    /// <summary>Another report on an existing spot — how a run of them becomes a pattern.</summary>
    private async Task<OccupancyMismatch> SeedMismatchOnAsync(Guid spotId, DateTimeOffset reportedAt)
    {
        var reporter = await SeedUserAsync();
        var mismatch = new OccupancyMismatch(
            spotId, Guid.NewGuid(), reporter, reportedAt.AddHours(-1), reportedAt.AddHours(1), reportedAt);
        await SeedAsync(db => db.OccupancyMismatches.Add(mismatch));
        return mismatch;
    }

    /// <summary>A user whose role carries the given review permissions — a notification audience of one.</summary>
    private async Task<Guid> SeedReviewerAsync(params string[] permissions)
    {
        var reviewer = new ApplicationUser
        {
            UserName = $"rev-{Guid.NewGuid():N}",
            Email = $"rev-{Guid.NewGuid():N}@test.local",
            DisplayName = "Kolega z dohledu",
        };
        var role = new ApplicationRole($"Reviewers-{Guid.NewGuid():N}");
        await SeedAsync(db =>
        {
            db.Users.Add(reviewer);
            db.Roles.Add(role);
            foreach (var permission in permissions)
            {
                db.RoleClaims.Add(new IdentityRoleClaim<Guid>
                {
                    RoleId = role.Id,
                    ClaimType = D3ParkingClaimTypes.Permission,
                    ClaimValue = permission,
                });
            }

            db.UserRoles.Add(new IdentityUserRole<Guid> { RoleId = role.Id, UserId = reviewer.Id });
        });
        return reviewer.Id;
    }

    /// <summary>Lets the next sweep send a digest instead of finding today's already gone out.</summary>
    private async Task ClearDigestMarkerAsync()
    {
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.ExecuteSqlRawAsync("UPDATE ParkingSettings SET LastOversightDigestUtc = NULL");
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

    /// <summary>A clock the spec moves by hand, so a deadline can be watched passing.</summary>
    private sealed class AdvancingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
