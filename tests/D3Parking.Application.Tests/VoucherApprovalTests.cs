using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// The hardened "I can't park" flow: a report needs a photo proof whose bytes can back exactly
/// one mismatch ever, and the apology voucher it grants holds no value until a spot manager —
/// never the reporter themselves — approves it. Runs against the SQL Server from
/// ConnectionStrings__SqlServer (skipped without it); a dedicated database per fixture.
/// </summary>
[TestFixture]
[NonParallelizable]
public class VoucherApprovalTests
{
    // Fixed instant, late enough in the local (UTC) day that the resident auto-share cutoff has
    // safely passed — mirrors the concurrency fixture.
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 20, 0, 0, TimeSpan.Zero);

    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private ReservationService _reservations = null!;
    private ParkingSpotService _spots = null!;
    private RecordingNotificationService _notifications = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the voucher approval tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_VoucherApprovalTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var factory = new TestDbContextFactory(_options);
        var parkingSettings = new FixedParkingSettings(IncentivePolicy.Default);
        var siteSettings = new FakeSiteSettings();
        var time = new FixedTimeProvider(Now);
        _notifications = new RecordingNotificationService();
        var messages = new PassthroughLocalizer<ParkingMessages>();

        _reservations = new ReservationService(factory, parkingSettings, siteSettings, time, _notifications, messages);
        _spots = new ParkingSpotService(factory, _notifications, siteSettings, time, messages);
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

    [SetUp]
    public void ResetNotifications() => _notifications.Sent.Clear();

    [Test]
    public async Task Report_without_photo_is_refused()
    {
        var (userId, reservation) = await SeedBlockedReservationAsync("V-01");

        var outcome = await _reservations.ReportBlockedSpotAsync(userId, reservation.Id, relocate: false, photo: null);

        Assert.That(outcome.Succeeded, Is.False);
        Assert.That(outcome.Error, Is.EqualTo("Parking_Error_PhotoRequired"));

        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.OccupancyMismatches.AnyAsync(m => m.ReporterId == userId), Is.False,
            "A refused report must record nothing.");
        var status = await db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync();
        Assert.That(status, Is.EqualTo(ReservationStatus.Reserved), "The booking must survive a refused report.");
    }

    [Test]
    public async Task Report_stores_photo_and_grants_pending_voucher()
    {
        var managerId = await SeedSpotManagerAsync();
        var (userId, reservation) = await SeedBlockedReservationAsync("V-02");
        var photo = Photo(2);

        var outcome = await _reservations.ReportBlockedSpotAsync(userId, reservation.Id, relocate: false, photo);

        Assert.That(outcome.Succeeded, Is.True, outcome.Error);
        Assert.That(outcome.VoucherGranted, Is.True);

        await using var db = new D3ParkingDbContext(_options);
        var mismatch = await db.OccupancyMismatches.SingleAsync(m => m.ReporterId == userId);
        var stored = await db.MismatchPhotos.SingleAsync(p => p.MismatchId == mismatch.Id);
        Assert.That(stored.ContentHash, Is.EqualTo(SHA256.HashData(photo.Content)),
            "The stored fingerprint must be the SHA-256 of the uploaded bytes.");
        Assert.That(stored.Content, Is.EqualTo(photo.Content));

        var voucher = await db.ApologyVouchers.SingleAsync(v => v.SourceMismatchId == mismatch.Id);
        Assert.That(voucher.Status, Is.EqualTo(ApologyVoucherStatus.PendingApproval),
            "A fresh voucher must wait for the spot manager's ruling.");

        Assert.That(_notifications.Sent, Does.Contain((userId, "Parking_Notify_VoucherGranted_Title")),
            "The reporter learns the voucher awaits approval.");
        Assert.That(_notifications.Sent, Does.Contain((managerId, "Parking_Notify_VoucherReview_Title")),
            "Every ManageSpots holder gets the review call to action.");
    }

    [Test]
    public async Task Same_photo_cannot_back_two_reports()
    {
        var (firstUser, firstReservation) = await SeedBlockedReservationAsync("V-03");
        var (secondUser, secondReservation) = await SeedBlockedReservationAsync("V-04");
        var photo = Photo(3);

        var first = await _reservations.ReportBlockedSpotAsync(firstUser, firstReservation.Id, relocate: false, photo);
        Assert.That(first.Succeeded, Is.True, first.Error);

        // The same bytes again — different driver, different reservation, same picture.
        var second = await _reservations.ReportBlockedSpotAsync(
            secondUser, secondReservation.Id, relocate: false, new BlockedSpotPhoto([.. photo.Content], photo.ContentType));

        Assert.That(second.Succeeded, Is.False);
        Assert.That(second.Error, Is.EqualTo("Parking_Error_PhotoReused"));

        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.OccupancyMismatches.AnyAsync(m => m.ReporterId == secondUser), Is.False,
            "A recycled photo must not void the booking or mint anything.");
        var status = await db.Reservations.Where(r => r.Id == secondReservation.Id).Select(r => r.Status).SingleAsync();
        Assert.That(status, Is.EqualTo(ReservationStatus.Reserved));
    }

    [Test]
    public async Task Pending_voucher_cannot_be_redeemed()
    {
        var (userId, reservation) = await SeedBlockedReservationAsync("V-05");
        var report = await _reservations.ReportBlockedSpotAsync(userId, reservation.Id, relocate: false, Photo(5));
        Assert.That(report.VoucherGranted, Is.True);

        var freeSpot = new ParkingSpot("V-05F", ParkingSpotType.Standard);
        await SeedAsync(db => db.ParkingSpots.Add(freeSpot));

        var booking = await _reservations.ReserveAsync(userId, freeSpot.Id, Now.AddHours(4), Now.AddHours(8), useVoucher: true);

        Assert.That(booking.Succeeded, Is.False, "An unapproved voucher holds no value yet.");
        Assert.That(booking.Errors, Does.Contain("Parking_Error_VoucherUnavailable"));
    }

    [Test]
    public async Task Approval_activates_voucher_and_restarts_validity()
    {
        var managerId = await SeedSpotManagerAsync();
        var (userId, reservation) = await SeedBlockedReservationAsync("V-06");
        await _reservations.ReportBlockedSpotAsync(userId, reservation.Id, relocate: false, Photo(6));
        var voucherId = await VoucherIdOfAsync(userId);

        var ruling = await _spots.ApproveVoucherAsync(voucherId, managerId);
        Assert.That(ruling.Succeeded, Is.True, ruling.Errors.FirstOrDefault());

        await using (var db = new D3ParkingDbContext(_options))
        {
            var voucher = await db.ApologyVouchers.SingleAsync(v => v.Id == voucherId);
            Assert.That(voucher.Status, Is.EqualTo(ApologyVoucherStatus.Approved));
            Assert.That(voucher.ReviewedById, Is.EqualTo(managerId));
            Assert.That(voucher.ExpiresAtUtc, Is.EqualTo(Now + ReservationService.ApologyVoucherValidity),
                "Validity restarts from the approval, not the grant.");
        }

        Assert.That(_notifications.Sent, Does.Contain((userId, "Parking_Notify_VoucherApproved_Title")));

        // The approved voucher now absorbs a booking's whole price: the wallet must not move.
        var freeSpot = new ParkingSpot("V-06F", ParkingSpotType.Standard);
        await SeedAsync(db => db.ParkingSpots.Add(freeSpot));
        var booking = await _reservations.ReserveAsync(userId, freeSpot.Id, Now.AddHours(4), Now.AddHours(8), useVoucher: true);
        Assert.That(booking.Succeeded, Is.True, booking.Errors.FirstOrDefault());

        await using (var db = new D3ParkingDbContext(_options))
        {
            var voucher = await db.ApologyVouchers.SingleAsync(v => v.Id == voucherId);
            Assert.That(voucher.RedeemedAtUtc, Is.Not.Null);
            var charged = await db.Reservations
                .Where(r => r.UserId == userId && r.SpotId == freeSpot.Id)
                .Select(r => r.CreditsCharged)
                .SingleAsync();
            Assert.That(charged, Is.EqualTo(0), "The voucher, not the wallet, pays the booking.");
        }
    }

    [Test]
    public async Task Reporter_cannot_review_their_own_voucher()
    {
        var (userId, reservation) = await SeedBlockedReservationAsync("V-07");
        await _reservations.ReportBlockedSpotAsync(userId, reservation.Id, relocate: false, Photo(7));
        var voucherId = await VoucherIdOfAsync(userId);

        var ruling = await _spots.ApproveVoucherAsync(voucherId, reviewerId: userId);

        Assert.That(ruling.Succeeded, Is.False);
        Assert.That(ruling.Errors, Does.Contain("Parking_Error_VoucherOwnReview"));

        await using var db = new D3ParkingDbContext(_options);
        var status = await db.ApologyVouchers.Where(v => v.Id == voucherId).Select(v => v.Status).SingleAsync();
        Assert.That(status, Is.EqualTo(ApologyVoucherStatus.PendingApproval));
    }

    [Test]
    public async Task Rejection_kills_the_voucher_but_not_the_next_honest_report()
    {
        var managerId = await SeedSpotManagerAsync();
        var (userId, reservation) = await SeedBlockedReservationAsync("V-08");
        await _reservations.ReportBlockedSpotAsync(userId, reservation.Id, relocate: false, Photo(8));
        var voucherId = await VoucherIdOfAsync(userId);

        var ruling = await _spots.RejectVoucherAsync(voucherId, managerId);
        Assert.That(ruling.Succeeded, Is.True, ruling.Errors.FirstOrDefault());
        Assert.That(_notifications.Sent, Does.Contain((userId, "Parking_Notify_VoucherRejected_Title")));
        Assert.That(await _reservations.GetMyApologyVoucherAsync(userId), Is.Null,
            "A rejected voucher must vanish from the driver's view.");

        // A second ruling on the same voucher resolves to the friendly "already decided".
        var again = await _spots.ApproveVoucherAsync(voucherId, managerId);
        Assert.That(again.Succeeded, Is.False);
        Assert.That(again.Errors, Does.Contain("Parking_Error_VoucherAlreadyReviewed"));

        // The rejection must not mute a later, possibly genuine report (fresh photo, same day).
        var (_, secondReservation) = await SeedBlockedReservationAsync("V-09", userId);
        var second = await _reservations.ReportBlockedSpotAsync(userId, secondReservation.Id, relocate: false, Photo(9));
        Assert.That(second.Succeeded, Is.True, second.Error);
        Assert.That(second.VoucherGranted, Is.True, "A rejected voucher must not block a new pending grant.");
    }

    /// <summary>A reserved spot whose window covers <see cref="Now"/>, so the report is in its legal window.</summary>
    private async Task<(Guid UserId, Reservation Reservation)> SeedBlockedReservationAsync(string spotCode, Guid? existingUserId = null)
    {
        var userId = existingUserId ?? Guid.NewGuid();
        var spot = new ParkingSpot(spotCode, ParkingSpotType.Standard);
        var reservation = new Reservation(spot.Id, userId, Now.AddHours(-1), Now.AddHours(2), false, Now.AddHours(-2), creditsCharged: 0);
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            if (existingUserId is null)
            {
                db.ParkerScores.Add(new ParkerScore(userId));
            }

            db.Reservations.Add(reservation);
        });
        return (userId, reservation);
    }

    /// <summary>A user whose role carries the ManageSpots permission claim — the review audience.</summary>
    private async Task<Guid> SeedSpotManagerAsync()
    {
        var manager = new ApplicationUser { UserName = $"manager-{Guid.NewGuid():N}", Email = "manager@test.local" };
        var role = new ApplicationRole($"SpotManagers-{Guid.NewGuid():N}");
        await SeedAsync(db =>
        {
            db.Users.Add(manager);
            db.Roles.Add(role);
            db.RoleClaims.Add(new IdentityRoleClaim<Guid>
            {
                RoleId = role.Id,
                ClaimType = D3ParkingClaimTypes.Permission,
                ClaimValue = Permissions.Parking.ManageSpots,
            });
            db.UserRoles.Add(new IdentityUserRole<Guid> { RoleId = role.Id, UserId = manager.Id });
        });
        return manager.Id;
    }

    private async Task<Guid> VoucherIdOfAsync(Guid userId)
    {
        await using var db = new D3ParkingDbContext(_options);
        return await db.ApologyVouchers
            .Where(v => v.UserId == userId && v.Status == ApologyVoucherStatus.PendingApproval)
            .Select(v => v.Id)
            .SingleAsync();
    }

    private async Task SeedAsync(Action<D3ParkingDbContext> seed)
    {
        await using var db = new D3ParkingDbContext(_options);
        seed(db);
        await db.SaveChangesAsync();
    }

    /// <summary>Distinct, valid photo payload per seed — same seed, same bytes (for reuse tests).</summary>
    private static BlockedSpotPhoto Photo(byte seed)
    {
        var content = new byte[256];
        content[0] = seed;
        for (var i = 1; i < content.Length; i++)
        {
            content[i] = (byte)(seed * 31 + i);
        }

        return new BlockedSpotPhoto(content, "image/jpeg");
    }

    private sealed class FixedParkingSettings(IncentivePolicy policy) : IParkingSettingsService
    {
        public Task<IncentivePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(policy);

        public Task<TimeSpan> GetSweepIntervalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GeoPoint?> GetLotLocationAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ParkingSettingsDto> GetAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ParkingResult> UpdateAsync(ParkingSettingsDto settings, Guid actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> AdaptPeakSurchargeAsync(double measuredOccupancy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
