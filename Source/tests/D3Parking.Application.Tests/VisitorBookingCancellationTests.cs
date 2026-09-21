using System.Data.Common;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Settings;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Infrastructure.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>Cancellation regressions against SQL Server in an isolated synthetic database.</summary>
[TestFixture]
[NonParallelizable]
public sealed class VisitorBookingCancellationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 27, 6, 0, 0, TimeSpan.Zero);
    private DbContextOptions<D3ParkingDbContext>? _options;
    private string _connectionString = string.Empty;
    private MemoryCache? _cache;
    private ParkingSettingsService _settings = null!;
    private SiteSettingsService _site = null!;
    private Guid _creator;
    private Guid _receptionist;

    [OneTimeSetUp]
    public async Task CreateDatabase()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
            Assert.Ignore("ConnectionStrings__SqlServer is not set; visitor cancellation requires real SQL Server.");
        _connectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_VisitorCancellationTests_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = Options();
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        _creator = await AddActor(db, Permissions.Parking.ManageVisitors);
        _receptionist = await AddActor(db, Permissions.Parking.ManageVisitors);
        db.SiteSettings.Add(SiteSettings.CreateDefault());
        db.ParkingSettings.Add(ParkingSettings.CreateDefault());
        await db.SaveChangesAsync();
        _cache = new MemoryCache(new MemoryCacheOptions());
        var factory = new TestDbContextFactory(_options);
        _site = new SiteSettingsService(factory, _cache, new PassthroughLocalizer<AccountMessages>(),
            new FixedTimeProvider(Now), NullLogger<SiteSettingsService>.Instance);
        _settings = new ParkingSettingsService(factory, _cache, _site,
            new FixedTimeProvider(Now), NullLogger<ParkingSettingsService>.Instance);
    }

    [SetUp]
    public async Task ResetBookings()
    {
        await using var db = new D3ParkingDbContext(_options!);
        await db.VisitorBookings.ExecuteDeleteAsync();
        await db.AccountAuditEvents.ExecuteDeleteAsync();
        await db.ParkingSpots.ExecuteDeleteAsync();
        await db.ParkingSettings.ExecuteDeleteAsync();
        db.ParkingSettings.Add(ParkingSettings.CreateDefault());
        await db.SaveChangesAsync();
    }

    [OneTimeTearDown]
    public async Task DeleteDatabase()
    {
        _cache?.Dispose();
        if (_options is null) return;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    [Test]
    public async Task Legacy_cancellation_without_an_authenticated_actor_fails_closed()
    {
        var booking = await AddBooking();
        var result = await Service().CancelAsync(booking.Id);
        Assert.That(result.Errors, Does.Contain("Parking_Error_AccessDenied"));
        await AssertUnchanged(booking);
    }

    [TestCase(Permissions.Parking.View, AccountStatus.Active)]
    [TestCase(Permissions.Parking.ManageVisitors, AccountStatus.Blocked)]
    [TestCase(Permissions.Parking.ManageVisitors, AccountStatus.Deactivated)]
    [TestCase(Permissions.Parking.ManageVisitors, AccountStatus.Suspended)]
    [TestCase(Permissions.Parking.ManageVisitors, AccountStatus.PendingActivation)]
    public async Task Direct_cancellation_checks_current_permission_and_active_account_without_disclosing_existence(
        string permission, AccountStatus status)
    {
        var booking = await AddBooking();
        await using var db = new D3ParkingDbContext(_options!);
        var actor = await AddActor(db, permission, status);
        var service = Service();
        Assert.That((await service.CancelCheckedAsync(booking.Id, actor)).Errors,
            Does.Contain("Parking_Error_AccessDenied"));
        Assert.That((await service.CancelCheckedAsync(Guid.NewGuid(), actor)).Errors,
            Does.Contain("Parking_Error_AccessDenied"));
        await AssertUnchanged(booking);
    }

    [Test]
    public async Task A_missing_actor_cannot_cancel_an_existing_booking()
    {
        var booking = await AddBooking();
        var result = await Service().CancelCheckedAsync(booking.Id, Guid.NewGuid());
        Assert.That(result.Errors, Does.Contain("Parking_Error_AccessDenied"));
        await AssertUnchanged(booking);
    }

    [Test]
    public async Task A_revoked_permission_is_rechecked_after_the_receptionist_loaded_the_booking()
    {
        var booking = await AddBooking();
        await using var db = new D3ParkingDbContext(_options!);
        var actor = await AddActor(db, Permissions.Parking.ManageVisitors);
        var service = Service();
        Assert.That(await service.ListUpcomingAsync(), Has.Count.EqualTo(1));
        await db.UserRoles.Where(r => r.UserId == actor).ExecuteDeleteAsync();
        Assert.That((await service.CancelCheckedAsync(booking.Id, actor)).Errors,
            Does.Contain("Parking_Error_AccessDenied"));
        await AssertUnchanged(booking);
    }

    [Test]
    public async Task ManageVisitors_can_cancel_another_receptionists_booking_and_preserves_its_history()
    {
        var booking = await AddBooking();
        Assert.That(booking.CreatedById, Is.Not.EqualTo(_receptionist));
        var service = Service();
        Assert.That((await service.CancelCheckedAsync(booking.Id, _receptionist)).Succeeded, Is.True);
        Assert.That(await service.ListUpcomingAsync(), Is.Empty);
        Assert.That(await service.GetFreeSpotsAsync(booking.StartUtc, booking.EndUtc), Has.Count.EqualTo(1));
        await using var db = new D3ParkingDbContext(_options!);
        var saved = await db.VisitorBookings.SingleAsync();
        var audit = await db.AccountAuditEvents.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(saved.Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
            Assert.That(saved.StartUtc, Is.EqualTo(booking.StartUtc));
            Assert.That(saved.EndUtc, Is.EqualTo(booking.EndUtc));
            Assert.That(saved.CreatedById, Is.EqualTo(booking.CreatedById));
            Assert.That(saved.CreatedAtUtc, Is.EqualTo(booking.CreatedAtUtc));
            Assert.That(audit.UserId, Is.EqualTo(_receptionist));
            Assert.That(audit.Actor, Is.EqualTo($"admin:{_receptionist}"));
            Assert.That(audit.Type, Is.EqualTo(AccountAuditEventType.ReservationOverridden));
            Assert.That(audit.OccurredAtUtc, Is.EqualTo(Now));
            Assert.That(audit.Detail, Does.Contain(booking.Id.ToString()).And.Contain(booking.SpotId.ToString())
                .And.Contain("cancelled").And.Contain(booking.StartUtc.ToString("O")).And.Contain(booking.EndUtc.ToString("O")));
            Assert.That(audit.Detail, Does.Not.Contain(booking.VisitorName)
                .And.Not.Contain(booking.Company!).And.Not.Contain(booking.LicensePlate!));
        });
    }

    [Test]
    public async Task Cancellation_uses_existing_booking_state_even_after_a_calendar_or_spot_change()
    {
        var booking = await AddBooking(activeSpot: false);
        await using var db = new D3ParkingDbContext(_options!);
        await db.ParkingSettings.ExecuteUpdateAsync(s => s.SetProperty(p => p.ReservationTimeMode, ReservationTimeMode.AllDay));
        Assert.That((await Service().CancelCheckedAsync(booking.Id, _receptionist)).Succeeded, Is.True);
        Assert.That((await db.VisitorBookings.SingleAsync()).Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
        Assert.That(await db.AccountAuditEvents.CountAsync(), Is.EqualTo(1));
    }

    [TestCase(-1)]
    [TestCase(0)]
    public async Task Ended_bookings_including_the_exact_end_boundary_cannot_be_rewritten(int secondsBeforeNow)
    {
        var booking = await AddBooking(Now.AddSeconds(secondsBeforeNow));
        var result = await Service().CancelCheckedAsync(booking.Id, _receptionist);
        Assert.That(result.Errors, Does.Contain("Parking_Visitor_Error_Ended"));
        await AssertUnchanged(booking);
    }

    [Test]
    public async Task An_ongoing_booking_can_be_cancelled_before_its_end()
    {
        var booking = await AddBooking(Now.AddSeconds(1));
        Assert.That(booking.StartUtc, Is.LessThan(Now));
        Assert.That((await Service().CancelCheckedAsync(booking.Id, _receptionist)).Succeeded, Is.True);
    }

    [Test]
    public async Task An_already_cancelled_booking_returns_a_conflict_without_a_second_audit_or_notification()
    {
        var booking = await AddBooking(host: _creator);
        var notifications = Probe();
        var probe = (NotificationProbe)(object)notifications;
        var service = Service(notifications: notifications);
        Assert.That((await service.CancelCheckedAsync(booking.Id, _receptionist)).Succeeded, Is.True);
        Assert.That((await service.CancelCheckedAsync(booking.Id, _receptionist)).Errors,
            Does.Contain("Parking_Visitor_Error_AlreadyCancelled"));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.AccountAuditEvents.CountAsync(), Is.EqualTo(1));
        Assert.That(probe.Attempts, Is.EqualTo(1));
    }

    [Test]
    public async Task An_authorized_unknown_id_returns_not_found_without_mutating_another_booking()
    {
        var booking = await AddBooking();
        Assert.That((await Service().CancelCheckedAsync(Guid.NewGuid(), _receptionist)).Errors,
            Does.Contain("Parking_Error_ReservationNotFound"));
        await AssertUnchanged(booking);
    }

    [Test]
    public async Task Independent_receptionists_cannot_both_cancel_the_same_booking()
    {
        var booking = await AddBooking(host: _creator);
        var gate = new BothBookingSnapshotsRead();
        var notifications = Probe();
        var results = await Task.WhenAll(
            Service(Options(gate), notifications).CancelCheckedAsync(booking.Id, _creator),
            Service(Options(gate), notifications).CancelCheckedAsync(booking.Id, _receptionist));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(gate.ReadCount, Is.GreaterThanOrEqualTo(2), "Both SQL transactions read the original state before writing.");
            Assert.That(results.Count(r => r.Succeeded), Is.EqualTo(1));
            Assert.That(results.Single(r => !r.Succeeded).Errors, Does.Contain("Parking_Visitor_Error_AlreadyCancelled"));
            Assert.That(db.VisitorBookings.Single().Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
            Assert.That(db.AccountAuditEvents.Count(), Is.EqualTo(1));
            Assert.That(((NotificationProbe)(object)notifications).Attempts, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_failure_after_save_before_commit_rolls_back_cancellation_and_audit()
    {
        var booking = await AddBooking(host: _creator);
        var notifications = Probe();
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Service(Options(new FailAfterSave()), notifications).CancelCheckedAsync(booking.Id, _receptionist));
        await AssertUnchanged(booking);
        Assert.That(((NotificationProbe)(object)notifications).Attempts, Is.Zero);
    }

    [Test]
    public async Task Host_notification_observes_a_committed_cancellation_and_failure_keeps_the_success_result()
    {
        var booking = await AddBooking(host: _creator);
        var notifications = Probe();
        var probe = (NotificationProbe)(object)notifications;
        var sawCommittedState = false;
        probe.OnNotifyAsync = async () =>
        {
            await using var db = new D3ParkingDbContext(_options!);
            sawCommittedState = (await db.VisitorBookings.SingleAsync()).Status == VisitorBookingStatus.Cancelled
                && await db.AccountAuditEvents.CountAsync() == 1;
            throw new InvalidOperationException("Synthetic notification transport failure.");
        };
        var result = await Service(notifications: notifications).CancelCheckedAsync(booking.Id, _receptionist);
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(probe.Attempts, Is.EqualTo(1));
            Assert.That(probe.LastRecipient, Is.EqualTo(_creator));
            Assert.That(sawCommittedState, Is.True);
        });
    }

    [Test]
    public async Task A_booking_with_a_missing_spot_can_still_be_cancelled_and_notify_its_host()
    {
        var booking = await AddBooking(host: _creator);
        await using var db = new D3ParkingDbContext(_options!);
        await db.ParkingSpots.Where(s => s.Id == booking.SpotId).ExecuteDeleteAsync();
        var notifications = Probe();
        var result = await Service(notifications: notifications).CancelCheckedAsync(booking.Id, _receptionist);
        var probe = (NotificationProbe)(object)notifications;
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(probe.Attempts, Is.EqualTo(1));
            Assert.That(probe.LastMessage, Is.EqualTo("Parking_Notify_VisitorCancelled_BodyWithoutSpot"));
            Assert.That(db.VisitorBookings.Single().Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
            Assert.That(db.AccountAuditEvents.Single().Detail, Does.Contain(booking.SpotId.ToString()));
        });
    }

    private async Task<VisitorBooking> AddBooking(DateTimeOffset? endUtc = null, Guid? host = null,
        bool cancelled = false, bool activeSpot = true)
    {
        await using var db = new D3ParkingDbContext(_options!);
        var spot = new ParkingSpot($"VC-{Guid.NewGuid():N}"[..12], ParkingSpotType.Visitor);
        if (!activeSpot) spot.Deactivate();
        db.ParkingSpots.Add(spot);
        var end = endUtc ?? Now.AddDays(1).AddHours(2);
        var booking = new VisitorBooking(spot.Id, "Synthetic guest", "Synthetic company", "SYNTHETIC",
            host, end.AddHours(-2), end, _creator, Now.AddDays(-2));
        if (cancelled) booking.Cancel();
        db.VisitorBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private async Task AssertUnchanged(VisitorBooking original)
    {
        await using var db = new D3ParkingDbContext(_options!);
        var booking = await db.VisitorBookings.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(booking.Status, Is.EqualTo(original.Status));
            Assert.That(booking.StartUtc, Is.EqualTo(original.StartUtc));
            Assert.That(booking.EndUtc, Is.EqualTo(original.EndUtc));
            Assert.That(booking.CreatedById, Is.EqualTo(original.CreatedById));
            Assert.That(db.AccountAuditEvents.Count(), Is.Zero);
        });
    }

    private static async Task<Guid> AddActor(D3ParkingDbContext db, string permission,
        AccountStatus status = AccountStatus.Active)
    {
        var user = new ApplicationUser { UserName = $"visitor-cancel-test-{Guid.NewGuid():N}", Status = status };
        var role = new ApplicationRole($"visitor-cancel-role-{Guid.NewGuid():N}");
        db.Users.Add(user);
        db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
        db.RoleClaims.Add(new IdentityRoleClaim<Guid>
        {
            RoleId = role.Id, ClaimType = D3ParkingClaimTypes.Permission, ClaimValue = permission,
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private DbContextOptions<D3ParkingDbContext> Options(params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(_connectionString).AddInterceptors(interceptors).Options;

    private VisitorBookingService Service(DbContextOptions<D3ParkingDbContext>? options = null,
        INotificationService? notifications = null) => new(
        new TestDbContextFactory(options ?? _options!), _settings, _site,
        notifications ?? new NullNotificationService(), new PassthroughLocalizer<ParkingMessages>(), new FixedTimeProvider(Now));

    private static INotificationService Probe() =>
        System.Reflection.DispatchProxy.Create<INotificationService, NotificationProbe>();

    public class NotificationProbe : System.Reflection.DispatchProxy
    {
        private int _attempts;
        public int Attempts => _attempts;
        public Guid? LastRecipient { get; private set; }
        public string? LastMessage { get; private set; }
        public Func<Task>? OnNotifyAsync { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(INotificationService.NotifyAsync))
            {
                Interlocked.Increment(ref _attempts);
                LastRecipient = (Guid)args![0]!;
                LastMessage = (string)args[4]!;
                return OnNotifyAsync?.Invoke() ?? Task.CompletedTask;
            }
            return targetMethod.Invoke(new NullNotificationService(), args);
        }
    }

    private sealed class BothBookingSnapshotsRead : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        public int ReadCount => _readCount;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [VisitorBookings]"))
            {
                var read = Interlocked.Increment(ref _readCount);
                if (read == 2) _continue.TrySetResult();
                if (read <= 2) await _continue.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic failure before commit.");
    }
}
