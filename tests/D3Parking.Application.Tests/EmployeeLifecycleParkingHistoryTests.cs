using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Administration;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>Verifies departure cleanup and its preview against isolated SQL Server data.</summary>
[TestFixture]
[NonParallelizable]
public sealed class EmployeeLifecycleParkingHistoryTests
{
    private DbContextOptions<D3ParkingDbContext>? _options;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; lifecycle history requires a real SQL Server.");
        }

        var connection = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_LifecycleHistoryTests_{Guid.NewGuid():N}",
        };
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(connection.ConnectionString).Options;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_options is not null)
        {
            await using var db = new D3ParkingDbContext(_options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Departure_preserves_started_and_finished_plans_and_refunds_only_future_plans_once(bool checkedIn)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var spot = new ParkingSpot($"HISTORY-{checkedIn}", ParkingSpotType.Standard);
        var past = new Reservation(spot.Id, userId, now.AddDays(-3), now.AddDays(-2), false, now.AddDays(-5), 10);
        var pastCheckedIn = new Reservation(spot.Id, userId, now.AddDays(-2), now.AddDays(-1), false, now.AddDays(-5), 10);
        pastCheckedIn.CheckIn(now.AddDays(-2));
        var endedNow = new Reservation(spot.Id, userId, now.AddHours(-1), now, false, now.AddDays(-5), 10);
        var future = new Reservation(spot.Id, userId, now.AddDays(1), now.AddDays(1).AddHours(1), false, now.AddDays(-5), 10);
        var running = new Reservation(spot.Id, userId, now, now.AddMinutes(30), false, now.AddDays(-5), 10);
        if (checkedIn) running.CheckIn(now);
        var all = new[] { past, pastCheckedIn, endedNow, future, running };
        var historicalIds = new[] { past.Id, pastCheckedIn.Id, endedNow.Id, running.Id };
        var originalVersions = all.ToDictionary(r => r.Id, r => (r.CalendarSequence, r.CalendarUpdatedAtUtc));
        var chargeIds = new List<Guid>();

        await using (var seed = new D3ParkingDbContext(_options!))
        {
            var score = new ParkerScore(userId);
            score.GrantCreditIfDue(100, ParkerScore.PeriodOf(now), now.AddDays(-5));
            seed.ParkingSpots.Add(spot);
            seed.Reservations.AddRange(all);
            foreach (var reservation in all)
            {
                score.ChargeCredits(reservation.CreditsCharged, now.AddDays(-5));
                var charge = new PointsLedgerEntry(userId, IncentiveReason.ReservationCharge, -10,
                    reservation.Id, now.AddDays(-5));
                chargeIds.Add(charge.Id);
                seed.PointsLedgerEntries.Add(charge);
            }
            seed.ParkerScores.Add(score);
            await seed.SaveChangesAsync();
        }

        await using (var preview = new D3ParkingDbContext(_options!))
        {
            var impact = await EmployeeLifecycleCleanup.PreviewAsync(preview, userId, CancellationToken.None);
            Assert.That(impact.ActiveReservations, Is.EqualTo(1),
                "Only a future booking may be cancelled; started and historical plans stay intact.");
        }

        // Both departure callers own the transaction. Repeat cleanup to cover synchronization retries.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var cleanup = new D3ParkingDbContext(_options!);
            await using var transaction = await cleanup.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await EmployeeLifecycleCleanup.CleanOperationalAsync(
                cleanup, userId, null, Guid.NewGuid(), now, revokeAccess: true, CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var verify = new D3ParkingDbContext(_options!);
        var saved = await verify.Reservations.Where(r => r.UserId == userId).ToDictionaryAsync(r => r.Id);
        var refunds = await verify.PointsLedgerEntries.Where(e => e.UserId == userId
            && e.Reason == IncentiveReason.ReservationRefund).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(saved.Count, Is.EqualTo(5));
            Assert.That(saved[past.Id].Status, Is.EqualTo(ReservationStatus.Reserved));
            Assert.That(saved[pastCheckedIn.Id].Status, Is.EqualTo(ReservationStatus.CheckedIn));
            Assert.That(saved[endedNow.Id].Status, Is.EqualTo(ReservationStatus.Reserved),
                "EndUtc is exclusive: a plan ending exactly at cleanup time is already history.");
            foreach (var id in historicalIds)
            {
                Assert.That((saved[id].CalendarSequence, saved[id].CalendarUpdatedAtUtc), Is.EqualTo(originalVersions[id]));
            }
            Assert.That(saved[future.Id].Status, Is.EqualTo(ReservationStatus.Cancelled));
            Assert.That(saved[running.Id].Status, Is.EqualTo(checkedIn ? ReservationStatus.CheckedIn : ReservationStatus.Reserved));
            Assert.That(refunds.Select(e => e.ReservationId), Is.EquivalentTo(new Guid?[] { future.Id }));
            Assert.That(refunds.Sum(e => e.Points), Is.EqualTo(10));
        });
        Assert.That(await verify.ParkerScores.Where(s => s.UserId == userId).Select(s => s.Credits).SingleAsync(), Is.EqualTo(60));
        Assert.That(await verify.PointsLedgerEntries.CountAsync(e => chargeIds.Contains(e.Id)
            && e.Reason == IncentiveReason.ReservationCharge && e.Points == -10), Is.EqualTo(5),
            "Original charges remain an unchanged historical trail.");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Departure_cancels_hosted_and_created_unfinished_visits_once_with_nonpersonal_audits(bool administrator)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        Guid? actorId = administrator ? Guid.NewGuid() : null;
        var spot = new ParkingSpot($"DEPART-{Guid.NewGuid():N}"[..24], ParkingSpotType.Visitor);
        VisitorBooking Visit(Guid? host, Guid creator, DateTimeOffset start, DateTimeOffset end) =>
            new(spot.Id, $"Synthetic visitor {Guid.NewGuid():N}", "Synthetic departure company", "SYNLEAVE",
                host, start, end, creator, now.AddDays(-5));

        var hosted = Visit(userId, otherUserId, now.AddDays(1), now.AddDays(1).AddHours(1));
        var createdForOtherHost = Visit(otherUserId, userId, now.AddDays(2), now.AddDays(2).AddHours(1));
        var hostedAndCreated = Visit(userId, userId, now.AddDays(3), now.AddDays(3).AddHours(1));
        var ongoing = Visit(userId, otherUserId, now.AddHours(-1), now.AddHours(1));
        var createdWithoutHost = Visit(null, userId, now.AddDays(4), now.AddDays(4).AddHours(1));
        var ended = Visit(otherUserId, userId, now.AddDays(-2), now.AddDays(-1));
        var endedExactlyNow = Visit(userId, otherUserId, now.AddHours(-1), now);
        var unrelated = Visit(otherUserId, otherUserId, now.AddDays(5), now.AddDays(5).AddHours(1));
        var cancelled = Visit(userId, userId, now.AddDays(6), now.AddDays(6).AddHours(1));
        cancelled.Cancel();
        var affected = new[] { hosted, createdForOtherHost, hostedAndCreated, ongoing, createdWithoutHost };
        var unchanged = new[] { ended, endedExactlyNow, unrelated, cancelled };
        var all = affected.Concat(unchanged).ToArray();
        var allIds = all.Select(v => v.Id).ToArray();

        await using (var seed = new D3ParkingDbContext(_options!))
        {
            seed.ParkingSpots.Add(spot);
            seed.VisitorBookings.AddRange(all);
            await seed.SaveChangesAsync();
        }

        await using (var preview = new D3ParkingDbContext(_options!))
        {
            var impact = await EmployeeLifecycleCleanup.PreviewAsync(preview, userId, CancellationToken.None);
            Assert.That(impact.UpcomingVisitorBookings, Is.EqualTo(affected.Length),
                "Departure includes visits created for other hosts, without double-counting the host/creator union.");
        }

        // Also exercise the helper's transaction when called without an outer lifecycle transaction.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var cleanup = new D3ParkingDbContext(_options!);
            await EmployeeLifecycleCleanup.CleanOperationalAsync(
                cleanup, userId, null, actorId, now, revokeAccess: true, CancellationToken.None);
        }

        await using var verify = new D3ParkingDbContext(_options!);
        var saved = await verify.VisitorBookings.Where(v => allIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id);
        var audits = await verify.AccountAuditEvents.Where(a => a.UserId == userId
            && a.Type == AccountAuditEventType.ReservationOverridden).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(saved.Count, Is.EqualTo(all.Length), "Departure must preserve visitor history.");
            Assert.That(audits.Count, Is.EqualTo(affected.Length), "Retries must not append a second cancellation audit.");
            foreach (var booking in affected)
            {
                Assert.That(saved[booking.Id].Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
                Assert.That(saved[booking.Id].HostUserId, Is.Null);
                var matched = audits.Where(a => a.Detail?.Contains(booking.Id.ToString(), StringComparison.Ordinal) == true).ToArray();
                Assert.That(matched.Length, Is.EqualTo(1), $"Exactly one audit must identify booking {booking.Id}.");
                if (matched.Length != 1) continue;
                var audit = matched[0];
                Assert.That(audit.Actor, Is.EqualTo(actorId.HasValue ? $"admin:{actorId.Value}" : "system"));
                Assert.That(audit.OccurredAtUtc, Is.EqualTo(now));
                Assert.That(audit.Detail, Does.Contain("employee departure"));
                Assert.That(audit.Detail, Does.Contain(spot.Id.ToString()));
                Assert.That(audit.Detail, Does.Contain(booking.StartUtc.ToString("O")));
                Assert.That(audit.Detail, Does.Contain(booking.EndUtc.ToString("O")));
                Assert.That(audit.Detail, Does.Not.Contain(booking.VisitorName));
                Assert.That(audit.Detail, Does.Not.Contain(booking.Company));
                Assert.That(audit.Detail, Does.Not.Contain(booking.LicensePlate));
            }
            foreach (var booking in unchanged)
            {
                Assert.That(saved[booking.Id].Status, Is.EqualTo(booking.Status));
                Assert.That(saved[booking.Id].HostUserId, Is.EqualTo(booking.HostUserId));
                Assert.That(audits.Any(a => a.Detail?.Contains(booking.Id.ToString(), StringComparison.Ordinal) == true), Is.False);
            }
            foreach (var booking in all)
            {
                Assert.That(saved[booking.Id].CreatedById, Is.EqualTo(booking.CreatedById));
                Assert.That(saved[booking.Id].StartUtc, Is.EqualTo(booking.StartUtc));
                Assert.That(saved[booking.Id].EndUtc, Is.EqualTo(booking.EndUtc));
            }
        });
        Assert.That((await EmployeeLifecycleCleanup.PreviewAsync(verify, userId, CancellationToken.None)).UpcomingVisitorBookings, Is.Zero);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Failure_after_persisting_departure_audit_rolls_back_visit_and_audit(bool outerTransaction)
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var spot = new ParkingSpot($"ROLL-{Guid.NewGuid():N}"[..24], ParkingSpotType.Visitor);
        var visitor = new VisitorBooking(spot.Id, "Synthetic rollback visitor", null, null, hostId,
            now.AddDays(1), now.AddDays(1).AddHours(1), userId, now);
        await using (var seed = new D3ParkingDbContext(_options!))
        {
            seed.ParkingSpots.Add(spot);
            seed.VisitorBookings.Add(visitor);
            await seed.SaveChangesAsync();
        }

        var interceptor = new FailAfterDepartureAuditSave();
        var options = new DbContextOptionsBuilder<D3ParkingDbContext>(_options!)
            .AddInterceptors(interceptor).Options;
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var cleanup = new D3ParkingDbContext(options);
            await using var transaction = outerTransaction
                ? await cleanup.Database.BeginTransactionAsync(IsolationLevel.Serializable)
                : null;
            await EmployeeLifecycleCleanup.CleanOperationalAsync(
                cleanup, userId, null, Guid.NewGuid(), now, revokeAccess: true, CancellationToken.None);
            if (transaction is not null) await transaction.CommitAsync();
        });
        Assert.That(interceptor.Triggered, Is.True, "The failure must happen after the departure audit reaches SQL Server.");

        await using var verify = new D3ParkingDbContext(_options!);
        var saved = await verify.VisitorBookings.SingleAsync(v => v.Id == visitor.Id);
        Assert.Multiple(() =>
        {
            Assert.That(saved.Status, Is.EqualTo(VisitorBookingStatus.Booked));
            Assert.That(saved.HostUserId, Is.EqualTo(hostId));
        });
        Assert.That(await verify.AccountAuditEvents.CountAsync(a => a.UserId == userId), Is.Zero,
            "A failed departure must not retain a cancellation or its audit.");
    }

    private sealed class FailAfterDepartureAuditSave : SaveChangesInterceptor
    {
        private bool _savingDepartureAudit;
        public bool Triggered { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            _savingDepartureAudit = eventData.Context!.ChangeTracker.Entries<AccountAuditEvent>()
                .Any(e => e.State == EntityState.Added && e.Entity.Type == AccountAuditEventType.ReservationOverridden
                    && e.Entity.Detail?.Contains("employee departure", StringComparison.Ordinal) == true);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (_savingDepartureAudit)
            {
                Triggered = true;
                throw new InvalidOperationException("Synthetic failure after saving departure audit before commit.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
