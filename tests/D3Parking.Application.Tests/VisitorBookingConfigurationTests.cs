using System.Data;
using System.Data.Common;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Domain.Settings;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Administration;
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

/// <summary>Real SQL Server regression tests. Only a newly named synthetic database is modified.</summary>
[TestFixture]
[NonParallelizable]
public sealed class VisitorBookingConfigurationTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly DateTimeOffset Now = SiteTime.At(new DateOnly(2026, 3, 27), new TimeOnly(6, 0), Prague);
    private DbContextOptions<D3ParkingDbContext>? _options;
    private string _connectionString = string.Empty;
    private MemoryCache? _cache;
    private ParkingSettingsService _settings = null!;
    private SiteSettingsService _site = null!;
    private Guid _receptionist;

    [OneTimeSetUp]
    public async Task CreateDatabase()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
            Assert.Ignore("ConnectionStrings__SqlServer is not set; visitor consistency requires real SQL Server.");
        _connectionString = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_VisitorTests_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = Options();
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        _receptionist = await AddActor(db, Permissions.Parking.ManageVisitors);
        var site = SiteSettings.CreateDefault();
        site.UpdateRegional(null, Prague.Id);
        db.SiteSettings.Add(site);
        await db.SaveChangesAsync();
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
        _cache?.Dispose();
        _cache = new MemoryCache(new MemoryCacheOptions());
        var factory = new TestDbContextFactory(_options!);
        _site = new SiteSettingsService(factory, _cache, new PassthroughLocalizer<AccountMessages>(),
            new FixedTimeProvider(Now), NullLogger<SiteSettingsService>.Instance);
        _settings = new ParkingSettingsService(factory, _cache, _site,
            new FixedTimeProvider(Now), NullLogger<ParkingSettingsService>.Instance);
    }

    [OneTimeTearDown]
    public async Task DeleteDatabase()
    {
        _cache?.Dispose();
        if (_options is null) return;
        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    [TestCase(ReservationTimeMode.AllDay, false)]
    [TestCase(ReservationTimeMode.TimeWindow, true)]
    public async Task The_configured_mode_is_enforced_in_availability_and_booking(ReservationTimeMode mode, bool allDay)
    {
        await ConfigureMode(mode);
        var spot = await AddSpot();
        var window = Window(allDay);
        var service = Service();
        var available = await service.GetFreeSpotsAsync(window.Start, window.End);
        var booked = await Book(service, spot.Id, window);
        Assert.Multiple(() =>
        {
            Assert.That(available, Is.Empty);
            Assert.That(booked.Errors, Does.Contain("Parking_Error_ReservationTimeModeChanged"));
        });
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.VisitorBookings.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Cached_old_policy_cannot_override_a_new_database_mode()
    {
        await ConfigureMode(ReservationTimeMode.TimeWindow);
        Assert.That((await _settings.GetPolicyAsync()).ReservationTimeMode, Is.EqualTo(ReservationTimeMode.TimeWindow));
        await ConfigureMode(ReservationTimeMode.AllDay);
        var spot = await AddSpot();
        var service = Service();
        var window = Window(false);
        var available = await service.GetFreeSpotsAsync(window.Start, window.End);
        var result = await Book(service, spot.Id, window);
        Assert.Multiple(() =>
        {
            Assert.That(available, Is.Empty);
            Assert.That(result.Errors, Does.Contain("Parking_Error_ReservationTimeModeChanged"));
        });
        Assert.That((await _settings.GetCurrentPolicyAsync()).ReservationTimeMode, Is.EqualTo(ReservationTimeMode.AllDay));
    }

    [TestCase(Permissions.Parking.View, AccountStatus.Active)]
    [TestCase(Permissions.Parking.ManageVisitors, AccountStatus.Blocked)]
    public async Task Direct_booking_checks_current_permission_and_account_status(string permission, AccountStatus status)
    {
        var spot = await AddSpot();
        await using var db = new D3ParkingDbContext(_options!);
        var actor = await AddActor(db, permission, status);
        var window = Window(false);
        var result = await Service().BookAsync(actor, spot.Id, window.Start, window.End, "Synthetic visitor", null, null, null);
        Assert.That(result.Errors, Does.Contain("Parking_Error_AccessDenied"));
        Assert.That(await db.VisitorBookings.CountAsync(), Is.Zero);
    }

    [TestCase(null)]
    [TestCase(AccountStatus.PendingActivation)]
    [TestCase(AccountStatus.Deactivated)]
    [TestCase(AccountStatus.Suspended)]
    [TestCase(AccountStatus.Blocked)]
    public async Task A_missing_or_inactive_host_cannot_receive_a_new_visitor_booking(AccountStatus? status)
    {
        await using var db = new D3ParkingDbContext(_options!);
        var host = status is { } existingStatus
            ? await AddActor(db, Permissions.Parking.View, existingStatus)
            : Guid.NewGuid();
        var spot = await AddSpot();
        var window = Window(false);
        var result = await Service().BookAsync(_receptionist, spot.Id, window.Start, window.End,
            "Synthetic visitor", null, null, host);
        Assert.Multiple(() =>
        {
            Assert.That(result.Errors, Does.Contain("Parking_Visitor_Error_HostUnavailable"));
            Assert.That(db.VisitorBookings.Count(), Is.Zero);
            Assert.That(db.AccountAuditEvents.Count(), Is.Zero);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task An_active_host_or_no_host_remains_valid(bool withHost)
    {
        await using var db = new D3ParkingDbContext(_options!);
        Guid? host = withHost ? await AddActor(db, Permissions.Parking.View) : null;
        var spot = await AddSpot();
        var window = Window(false);
        var result = await Service().BookAsync(_receptionist, spot.Id, window.Start, window.End,
            "Synthetic visitor", null, null, host);
        Assert.That(result.Succeeded, Is.True);
        Assert.That((await db.VisitorBookings.SingleAsync()).HostUserId, Is.EqualTo(host));
        Assert.That(await db.AccountAuditEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Host_departure_waits_for_a_booking_that_has_read_the_host_then_cancels_it()
    {
        await using var seed = new D3ParkingDbContext(_options!);
        var host = await AddActor(seed, Permissions.Parking.View);
        var spot = await AddSpot();
        var window = Window(false);
        var gate = new PauseAfterPolicyRead();
        var departureCommand = new HostDeactivationCommandStarted();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var booking = Service(Options(gate)).BookAsync(_receptionist, spot.Id, window.Start, window.End,
            "Synthetic visitor", null, null, host, timeout.Token);
        await gate.Read.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var departing = DepartAsync();
        var departureWaited = false;
        try
        {
            await departureCommand.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            departureWaited = await Task.WhenAny(departing, Task.Delay(150)) != departing;
        }
        finally
        {
            gate.Continue.TrySetResult();
            try
            {
                await Task.WhenAll(booking, departing).WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch
            {
                await timeout.CancelAsync();
                throw;
            }
        }

        Assert.That(departureWaited, Is.True, "The host read must remain locked until the booking commits.");
        Assert.That((await booking.WaitAsync(TimeSpan.FromSeconds(1))).Succeeded, Is.True);
        await using var verify = new D3ParkingDbContext(_options!);
        Assert.That((await verify.VisitorBookings.SingleAsync()).Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
        Assert.That((await Service().BookAsync(_receptionist, spot.Id, window.Start, window.End,
            "Synthetic visitor", null, null, host)).Errors, Does.Contain("Parking_Visitor_Error_HostUnavailable"));
        Assert.That(await verify.VisitorBookings.CountAsync(), Is.EqualTo(1));

        async Task DepartAsync()
        {
            await using var departure = new D3ParkingDbContext(Options(departureCommand));
            await using var transaction = await departure.Database.BeginTransactionAsync(IsolationLevel.Serializable, timeout.Token);
            await departure.Users.Where(u => u.Id == host)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.Blocked), timeout.Token);
            await EmployeeLifecycleCleanup.CleanOperationalAsync(departure, host, null, _receptionist,
                Now, revokeAccess: true, timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }
    }

    [Test]
    public async Task Creation_has_an_atomic_audit_without_visitor_personal_details()
    {
        var spot = await AddSpot();
        var result = await Book(Service(), spot.Id, Window(false));
        Assert.That(result.Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options!);
        var booking = await db.VisitorBookings.SingleAsync();
        var audit = await db.AccountAuditEvents.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(audit.Actor, Is.EqualTo($"admin:{_receptionist}"));
            Assert.That(audit.Detail, Does.Contain(booking.Id.ToString()).And.Contain(spot.Id.ToString()));
            Assert.That(audit.Detail, Does.Not.Contain("Synthetic visitor"));
        });
    }

    [Test]
    public async Task Independent_receptionists_cannot_both_book_the_same_interval()
    {
        var spot = await AddSpot();
        // Both SQL conflict queries observe the empty range before either transaction may save.
        // Their range locks must resolve the race even across independent service instances.
        var gate = new BothConflictQueriesRead();
        var options = Options(gate);
        var results = await Task.WhenAll(Book(Service(options), spot.Id, Window(false)), Book(Service(options), spot.Id, Window(false)));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(gate.ReadCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(results.Count(r => r.Succeeded), Is.EqualTo(1));
            Assert.That(results.Single(r => !r.Succeeded).Errors.Any(e => e is "Parking_Error_SpotConflict" or "Parking_Error_ConcurrentChange"), Is.True);
            Assert.That(db.VisitorBookings.Count(), Is.EqualTo(1));
            Assert.That(db.AccountAuditEvents.Count(), Is.EqualTo(1));
        });
    }

    [TestCase(ReservationTimeMode.AllDay, true)]
    [TestCase(ReservationTimeMode.TimeWindow, false)]
    public async Task Both_modes_can_create_an_appropriate_booking(ReservationTimeMode mode, bool allDay)
    {
        await ConfigureMode(mode);
        var spot = await AddSpot();
        var window = Window(allDay);
        var service = Service();
        Assert.That(await service.GetFreeSpotsAsync(window.Start, window.End), Has.Count.EqualTo(1));
        Assert.That((await Book(service, spot.Id, window)).Succeeded, Is.True);
        Assert.That(await service.GetFreeSpotsAsync(window.Start, window.End), Is.Empty);
        var listed = await service.ListUpcomingAsync();
        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(listed[0].StartUtc, Is.EqualTo(window.Start));
            Assert.That(listed[0].EndUtc, Is.EqualTo(window.End));
        });
    }

    [TestCase(3, 29, 23)]
    [TestCase(10, 25, 25)]
    public async Task All_day_booking_uses_local_midnights_across_daylight_saving(int month, int day, int hours)
    {
        await ConfigureMode(ReservationTimeMode.AllDay);
        await using var db = new D3ParkingDbContext(_options!);
        await db.ParkingSettings.ExecuteUpdateAsync(s => s.SetProperty(p => p.ReservationHorizonDays, 366));
        var spot = await AddSpot();
        var window = Window(true, new DateOnly(2026, month, day));
        var result = await Book(Service(), spot.Id, window);
        var saved = await db.VisitorBookings.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(saved.EndUtc - saved.StartUtc, Is.EqualTo(TimeSpan.FromHours(hours)));
            Assert.That(ReservationWindowRules.IsFullLocalDay(saved.StartUtc, saved.EndUtc, Prague), Is.True);
        });
    }

    [Test]
    public async Task Adjacent_intervals_are_allowed_overlaps_are_not_and_cancellation_frees_the_interval()
    {
        var spot = await AddSpot();
        var window = Window(false);
        var service = Service();
        Assert.That((await Book(service, spot.Id, window)).Succeeded, Is.True);
        var overlap = await Book(service, spot.Id, (window.Start.AddMinutes(30), window.End.AddMinutes(30)));
        var adjacent = await Book(service, spot.Id, (window.End, window.End.AddHours(1)));
        Assert.Multiple(() =>
        {
            Assert.That(overlap.Errors, Does.Contain("Parking_Error_SpotConflict"));
            Assert.That(adjacent.Succeeded, Is.True);
        });
        var first = (await service.ListUpcomingAsync()).First();
        Assert.That((await service.CancelCheckedAsync(first.Id, _receptionist)).Succeeded, Is.True);
        Assert.That(await service.GetFreeSpotsAsync(window.Start, window.End), Has.Count.EqualTo(1));
        Assert.That((await Book(service, spot.Id, window)).Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That((await db.VisitorBookings.FindAsync(first.Id))!.Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
    }

    [Test]
    public async Task Time_window_may_end_at_midnight_but_may_not_span_two_dates()
    {
        var spot = await AddSpot();
        var day = new DateOnly(2026, 3, 28);
        var start = SiteTime.At(day, new TimeOnly(22, 0), Prague);
        var midnight = SiteTime.Day(day, Prague).End;
        var service = Service();
        Assert.That((await Book(service, spot.Id, (start, midnight))).Succeeded, Is.True);
        Assert.That((await Book(service, spot.Id, (start, midnight.AddHours(1)))).Errors,
            Does.Contain("Parking_Error_ReservationTimeModeChanged"));
    }

    [TestCase("same-day", "Parking_Error_SameDayReservationsNotAllowed")]
    [TestCase("weekday", "Parking_Error_ReservationWeekdayNotAllowed")]
    [TestCase("holiday", "Parking_Error_PublicHolidayNotAllowed")]
    [TestCase("horizon", "Parking_Error_ReservationHorizon")]
    public async Task Calendar_restrictions_are_enforced_even_after_the_policy_was_cached(string rule, string error)
    {
        _ = await _settings.GetPolicyAsync();
        await using var db = new D3ParkingDbContext(_options!);
        var date = new DateOnly(2026, 3, 28);
        switch (rule)
        {
            case "same-day":
                await db.ParkingSettings.ExecuteUpdateAsync(s => s.SetProperty(p => p.SameDayReservationsAllowed, false));
                date = SiteTime.Today(Now, Prague);
                break;
            case "weekday":
                await db.ParkingSettings.ExecuteUpdateAsync(s => s.SetProperty(p => p.AllowedReservationWeekdays, Weekday.Workdays));
                break;
            case "holiday":
                date = new DateOnly(2026, 4, 6); // Easter Monday; inside the existing 14-day horizon.
                break;
            case "horizon":
                date = SiteTime.Today(Now, Prague).AddDays(15);
                break;
        }
        var spot = await AddSpot();
        var window = Window(false, date);
        var service = Service();
        Assert.That(await service.GetFreeSpotsAsync(window.Start, window.End), Is.Empty);
        Assert.That((await Book(service, spot.Id, window)).Errors, Does.Contain(error));
        Assert.That(await db.VisitorBookings.CountAsync(), Is.Zero);
    }

    [TestCase(ParkingSpotType.Visitor, false, "Parking_Error_SpotNotFound")]
    [TestCase(ParkingSpotType.Standard, true, "Parking_Visitor_Error_NotVisitorSpot")]
    public async Task Forged_spot_id_cannot_book_an_inactive_or_employee_spot(ParkingSpotType type, bool active, string error)
    {
        var spot = await AddSpot(type, active);
        var window = Window(false);
        var service = Service();
        Assert.That(await service.GetFreeSpotsAsync(window.Start, window.End), Is.Empty);
        Assert.That((await Book(service, spot.Id, window)).Errors, Does.Contain(error));
    }

    [TestCase(129, 0, 0)]
    [TestCase(5, 129, 0)]
    [TestCase(5, 0, 17)]
    public async Task Oversized_details_are_validation_errors_instead_of_sql_errors(int nameLength, int companyLength, int plateLength)
    {
        var spot = await AddSpot();
        var window = Window(false);
        var result = await Service().BookAsync(_receptionist, spot.Id, window.Start, window.End,
            new string('N', nameLength), new string('C', companyLength), new string('P', plateLength), null);
        Assert.That(result.Errors, Does.Contain("Parking_Visitor_Error_DetailsTooLong"));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.VisitorBookings.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task A_revoked_permission_is_rechecked_when_the_form_submits()
    {
        await using var db = new D3ParkingDbContext(_options!);
        var actor = await AddActor(db, Permissions.Parking.ManageVisitors);
        var spot = await AddSpot();
        var service = Service();
        var window = Window(false);
        Assert.That(await service.GetFreeSpotsAsync(window.Start, window.End), Has.Count.EqualTo(1));
        await db.UserRoles.Where(r => r.UserId == actor).ExecuteDeleteAsync();
        var result = await service.BookAsync(actor, spot.Id, window.Start, window.End, "Synthetic visitor", null, null, null);
        Assert.That(result.Errors, Does.Contain("Parking_Error_AccessDenied"));
        Assert.That(await db.VisitorBookings.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Failure_after_saving_before_commit_rolls_back_booking_and_audit()
    {
        var spot = await AddSpot();
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Book(Service(Options(new FailAfterSave())), spot.Id, Window(false)));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.VisitorBookings.CountAsync(), Is.Zero);
        Assert.That(await db.AccountAuditEvents.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task A_concurrent_settings_write_waits_for_the_booking_transaction()
    {
        var spot = await AddSpot();
        var gate = new PauseAfterPolicyRead();
        var booking = Book(Service(Options(gate)), spot.Id, Window(false));
        await gate.Read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var updating = ConfigureMode(ReservationTimeMode.AllDay);
        try
        {
            var completed = await Task.WhenAny(updating, Task.Delay(150));
            Assert.That(completed, Is.Not.SameAs(updating), "The current policy remains locked until this booking decides and commits.");
        }
        finally
        {
            gate.Continue.TrySetResult();
        }
        Assert.That((await booking).Succeeded, Is.True);
        await updating;
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.VisitorBookings.CountAsync(), Is.EqualTo(1), "The booking ordered before the setting change remains historical data.");
        Assert.That((await _settings.GetCurrentPolicyAsync()).ReservationTimeMode, Is.EqualTo(ReservationTimeMode.AllDay));
        Assert.That((await Book(Service(), spot.Id, Window(false, new DateOnly(2026, 3, 30)))).Errors,
            Does.Contain("Parking_Error_ReservationTimeModeChanged"));
    }

    [Test]
    public async Task A_mode_change_preserves_existing_visits_and_their_overlap_protection()
    {
        var spot = await AddSpot();
        var service = Service();
        Assert.That((await Book(service, spot.Id, Window(false))).Succeeded, Is.True);
        var changed = (await _settings.GetAsync()) with { ReservationTimeMode = ReservationTimeMode.AllDay };
        var impact = await _settings.GetCalendarChangeImpactAsync(changed);
        Assert.That(impact.VisitorBookings, Is.Zero, "A mode change has never implicitly cancelled existing visitors.");
        Assert.That((await _settings.UpdateAsync(changed, _receptionist)).Succeeded, Is.True);
        var window = Window(true);
        Assert.That(await service.GetFreeSpotsAsync(window.Start, window.End), Is.Empty);
        Assert.That((await Book(service, spot.Id, window)).Errors, Does.Contain("Parking_Error_SpotConflict"));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That((await db.VisitorBookings.SingleAsync()).Status, Is.EqualTo(VisitorBookingStatus.Booked));
    }

    [Test]
    public async Task A_host_notification_failure_does_not_change_the_committed_booking_result()
    {
        var spot = await AddSpot();
        var notifications = System.Reflection.DispatchProxy.Create<INotificationService, VisitorNotificationFailureProxy>();
        var window = Window(false);
        var result = await Service(notifications: notifications).BookAsync(_receptionist, spot.Id,
            window.Start, window.End, "Synthetic visitor", null, null, _receptionist);
        Assert.That(result.Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.VisitorBookings.CountAsync(), Is.EqualTo(1));
        Assert.That(await db.AccountAuditEvents.CountAsync(), Is.EqualTo(1));
        Assert.That(((VisitorNotificationFailureProxy)(object)notifications).Attempts, Is.EqualTo(1),
            "Post-commit notification delivery is outside the transaction retry boundary.");
    }

    [Test]
    public async Task Missing_policy_uses_application_defaults_without_creating_settings_on_the_booking_read_path()
    {
        await using var db = new D3ParkingDbContext(_options!);
        await db.ParkingSettings.ExecuteDeleteAsync();
        var spot = await AddSpot();
        var service = Service();
        var window = Window(false);
        Assert.That(await service.GetFreeSpotsAsync(window.Start, window.End), Has.Count.EqualTo(1));
        Assert.That((await Book(service, spot.Id, window)).Succeeded, Is.True);
        Assert.That(await db.ParkingSettings.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task An_out_of_horizon_final_calendar_date_is_rejected_without_overflow()
    {
        var start = new DateTimeOffset(9999, 12, 31, 8, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        var spot = await AddSpot();
        var service = Service();
        Assert.That(await service.GetFreeSpotsAsync(start, end), Is.Empty);
        var result = await Book(service, spot.Id, (start, end));
        Assert.That(result.Errors, Does.Contain("Parking_Error_ReservationHorizon"));
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Window(bool allDay, DateOnly? date = null)
    {
        var day = date ?? new DateOnly(2026, 3, 28);
        return allDay ? SiteTime.Day(day, Prague)
            : (SiteTime.At(day, new TimeOnly(8, 0), Prague), SiteTime.At(day, new TimeOnly(10, 0), Prague));
    }

    private Task<ParkingResult> Book(VisitorBookingService service, Guid spot, (DateTimeOffset Start, DateTimeOffset End) window) =>
        service.BookAsync(_receptionist, spot, window.Start, window.End, "Synthetic visitor", null, null, null);

    private async Task ConfigureMode(ReservationTimeMode mode)
    {
        await using var db = new D3ParkingDbContext(_options!);
        await db.ParkingSettings.ExecuteUpdateAsync(s => s.SetProperty(p => p.ReservationTimeMode, mode));
    }

    private async Task<ParkingSpot> AddSpot(ParkingSpotType type = ParkingSpotType.Visitor, bool active = true)
    {
        await using var db = new D3ParkingDbContext(_options!);
        var spot = new ParkingSpot($"VIS-{Guid.NewGuid():N}"[..12], type);
        if (!active) spot.Deactivate();
        db.ParkingSpots.Add(spot);
        await db.SaveChangesAsync();
        return spot;
    }

    private static async Task<Guid> AddActor(D3ParkingDbContext db, string permission, AccountStatus status = AccountStatus.Active)
    {
        var user = new ApplicationUser { UserName = $"visitor-test-{Guid.NewGuid():N}", Status = status };
        var role = new ApplicationRole($"visitor-role-{Guid.NewGuid():N}");
        db.Users.Add(user);
        db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
        db.RoleClaims.Add(new IdentityRoleClaim<Guid> { RoleId = role.Id, ClaimType = D3ParkingClaimTypes.Permission, ClaimValue = permission });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private DbContextOptions<D3ParkingDbContext> Options(params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(_connectionString).AddInterceptors(interceptors).Options;

    private VisitorBookingService Service(DbContextOptions<D3ParkingDbContext>? options = null, INotificationService? notifications = null) => new(
        new TestDbContextFactory(options ?? _options!), _settings, _site,
        notifications ?? new NullNotificationService(), new PassthroughLocalizer<ParkingMessages>(), new FixedTimeProvider(Now));

    private sealed class BothConflictQueriesRead : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        public int ReadCount => _readCount;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [VisitorBookings]") && command.CommandText.Contains("EXISTS"))
            {
                var read = Interlocked.Increment(ref _readCount);
                if (read == 2) _continue.TrySetResult();
                if (read <= 2) await _continue.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class PauseAfterPolicyRead : DbCommandInterceptor
    {
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [ParkingSettings]"))
            {
                Read.TrySetResult();
                await Continue.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class HostDeactivationCommandStarted : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE ", StringComparison.Ordinal)
                && command.CommandText.Contains("[AspNetUsers]", StringComparison.Ordinal)
                && command.CommandText.Contains("[Status]", StringComparison.Ordinal))
            {
                Started.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic failure before commit.");
    }

    public class VisitorNotificationFailureProxy : System.Reflection.DispatchProxy
    {
        public int Attempts { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(INotificationService.NotifyAsync))
            {
                Attempts++;
                return Task.FromException(new InvalidOperationException("Synthetic notification transport failure."));
            }
            return targetMethod.Invoke(new NullNotificationService(), args);
        }
    }
}
