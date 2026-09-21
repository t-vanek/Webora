using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

/// <summary>
/// Real-browser visitor booking against the disposable application's SQL database. Settings are
/// read freshly by the visitor page, so these cases also exercise a policy change in an open form.
/// </summary>
[TestFixture]
[NonParallelizable]
public class VisitorBookingTests : AdminTest
{
    private DbContextOptions<D3ParkingDbContext>? _options;
    private readonly Dictionary<string, object?> _originalSettings = [];
    private bool _provisioned;
    private Guid _spotId;
    private Guid _actorId;
    private string _spotCode = string.Empty;
    private string _visitorName = string.Empty;
    private DateOnly _day;
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;

    private ILocator Dialog => Page.Locator(".visitors-create-dialog");
    private ILocator BookButton => Dialog.Locator("#visitors-book button");
    private ILocator NameInput => Dialog.Locator("#visitors-name input");
    private ILocator CancelDialog => Page.Locator(".visitors-cancel-dialog");
    private ILocator ConfirmCancelButton => CancelDialog.Locator("#visitors-confirm-cancel button");
    private ILocator KeepBookingButton => CancelDialog.Locator("#visitors-keep-booking button");

    [OneTimeSetUp]
    public async Task ProvisionVisitorsAsync()
    {
        if (Environment.GetEnvironmentVariable("D3PARKING_E2E_PROVISION") != "1")
            Assert.Ignore("Set D3PARKING_E2E_PROVISION=1 to provision the isolated visitor UI fixture.");
        if (!Uri.TryCreate(WebAppFixture.BaseUrl, UriKind.Absolute, out var url) || !url.IsLoopback)
            Assert.Fail("Visitor fixture provisioning is restricted to a loopback application URL.");
        var configured = WebAppFixture.IsolatedSqlConnection
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
            Assert.Fail("Visitor fixture provisioning requires the local test application's SQL connection.");
        var connection = new SqlConnectionStringBuilder(configured);
        if (WebAppFixture.IsolatedSqlConnection is null
            && !connection.InitialCatalog.StartsWith("D3Parking_LocalTest", StringComparison.Ordinal))
            Assert.Fail("Visitor fixture provisioning requires a D3Parking_LocalTest database.");
        var dataSource = connection.DataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
            ? connection.DataSource[4..] : connection.DataSource;
        var sqlHost = dataSource.Split(',', '\\')[0].Trim().Trim('[', ']');
        var localSql = sqlHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || sqlHost is "." or "(local)" or "(localdb)"
            || IPAddress.TryParse(sqlHost, out var address) && IPAddress.IsLoopback(address);
        if (!localSql)
            Assert.Fail("Visitor fixture provisioning is restricted to a loopback SQL Server.");

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(connection.ConnectionString).Options;
        await using var db = new D3ParkingDbContext(_options);
        var settings = await db.ParkingSettings.SingleAsync(s => s.Id == ParkingSettings.SingletonId);
        var fixtureSettings = new Dictionary<string, object?>
        {
            [nameof(ParkingSettings.ReservationTimeMode)] = ReservationTimeMode.AllDay,
            [nameof(ParkingSettings.ReservationHorizonDays)] = 30,
            [nameof(ParkingSettings.AllowedReservationWeekdays)] = Weekday.Everyday,
            [nameof(ParkingSettings.SameDayReservationsAllowed)] = true,
            [nameof(ParkingSettings.PublicHolidayReservationsAllowed)] = true,
        };
        foreach (var (name, value) in fixtureSettings)
        {
            _originalSettings[name] = db.Entry(settings).Property(name).CurrentValue;
            db.Entry(settings).Property(name).CurrentValue = value;
        }

        var timeZoneId = await db.SiteSettings.Select(s => s.DefaultTimeZoneId).SingleAsync();
        _timeZone = !string.IsNullOrWhiteSpace(timeZoneId)
            ? TimeZoneInfo.FindSystemTimeZoneById(timeZoneId) : TimeZoneInfo.Local;
        _day = SiteTime.Today(DateTimeOffset.UtcNow, _timeZone).AddDays(2);
        _actorId = await db.Users.Where(u => u.Email == Admin.Email).Select(u => u.Id).SingleAsync();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        _spotCode = $"E2E-V-{suffix}";
        _visitorName = $"E2E Visitor {suffix}";
        var spot = new ParkingSpot(_spotCode, ParkingSpotType.Visitor);
        _spotId = spot.Id;
        db.ParkingSpots.Add(spot);
        await db.SaveChangesAsync();
        _provisioned = true;
    }

    [SetUp]
    public async Task ClearOwnBookingsAsync()
    {
        await using var db = new D3ParkingDbContext(_options!);
        await DeleteOwnBookingsAndAuditsAsync(db);
    }

    [OneTimeTearDown]
    public async Task RestoreVisitorsAsync()
    {
        if (!_provisioned || _options is null) return;
        await using var db = new D3ParkingDbContext(_options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var settings = await db.ParkingSettings.SingleAsync(s => s.Id == ParkingSettings.SingletonId);
        foreach (var (name, value) in _originalSettings)
            db.Entry(settings).Property(name).CurrentValue = value;
        // Only this fixture's spot, bookings and their audit events are removed, including a
        // failed UI request's effects. No shared visitor records, users or database are deleted.
        await DeleteOwnBookingsAndAuditsAsync(db);
        await db.ParkingSpots.Where(s => s.Id == _spotId).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        // Other application areas still use the 30-second policy cache. The database must be
        // disposable and exclusive: background jobs can observe this temporary global policy.
        await Task.Delay(TimeSpan.FromSeconds(31));
    }

    [TestCase(ReservationTimeMode.AllDay)]
    [TestCase(ReservationTimeMode.TimeWindow)]
    public async Task A_new_form_prefills_a_valid_window_and_rechecks_calendar_settings_when_reopened(
        ReservationTimeMode reopenedMode)
    {
        await SetModeAsync(reopenedMode == ReservationTimeMode.AllDay
            ? ReservationTimeMode.TimeWindow : ReservationTimeMode.AllDay);
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/visitors");
        await Page.Locator("#visitors-open-create button").ClickAsync();
        await Expect(Dialog).ToBeVisibleAsync();
        await Expect(Dialog).Not.ToContainTextAsync("Nelze rezervovat čas v minulosti.");
        await Expect(Dialog.Locator("#visitors-spot")).ToContainTextAsync(_spotCode);
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Zavřít", Exact = true }).ClickAsync();

        // Change the shared calendar after loading the page, before opening the next form.
        // Only one weekday is allowed; its next occurrence is always two local days away.
        await using var db = new D3ParkingDbContext(_options!);
        var allowedDay = _day.DayOfWeek.ToWeekday();
        await db.ParkingSettings.Where(s => s.Id == ParkingSettings.SingletonId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.SameDayReservationsAllowed, false)
                .SetProperty(p => p.AllowedReservationWeekdays, allowedDay)
                .SetProperty(p => p.ReservationTimeMode, reopenedMode));
        try
        {
            await Page.Locator("#visitors-open-create button").ClickAsync();
            await Expect(Dialog.Locator("#visitors-date"))
                .ToHaveValueAsync(_day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await Expect(Dialog.Locator("input[type=time]"))
                .ToHaveCountAsync(reopenedMode == ReservationTimeMode.AllDay ? 0 : 2);
            await Expect(Dialog.Locator("#visitors-spot")).ToContainTextAsync(_spotCode);
        }
        finally
        {
            await db.ParkingSettings.Where(s => s.Id == ParkingSettings.SingletonId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.SameDayReservationsAllowed, true)
                    .SetProperty(p => p.AllowedReservationWeekdays, Weekday.Everyday));
        }
    }

    [Test]
    public async Task All_day_mode_asks_only_for_date_and_saves_the_whole_site_day()
    {
        await OpenBookingAsync(ReservationTimeMode.AllDay);
        await Expect(Dialog.Locator("input[type=time]")).ToHaveCountAsync(0);
        await Expect(Dialog).ToContainTextAsync(new Regex("celý den", RegexOptions.IgnoreCase));
        var spotChoice = Dialog.Locator("#visitors-spot");
        var hostChoice = Dialog.Locator("#visitors-host");
        await Expect(spotChoice).ToContainTextAsync("Vyberte místo");
        await Expect(spotChoice).ToHaveJSPropertyAsync("value", string.Empty);
        await Expect(hostChoice).ToContainTextAsync("Bez hostitele (volitelné)");
        await Expect(hostChoice).ToHaveJSPropertyAsync("value", string.Empty);
        await Expect(BookButton).ToBeDisabledAsync();
        await SelectOwnSpotAndNameAsync();
        await BookButton.ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0);
        await Expect(Page.Locator(".visitors-page")).ToContainTextAsync("Návštěva má rezervované místo.");

        await using var db = new D3ParkingDbContext(_options!);
        var booking = await db.VisitorBookings.SingleAsync(b => b.SpotId == _spotId);
        var expected = SiteTime.Day(_day, _timeZone);
        Assert.Multiple(() =>
        {
            Assert.That(booking.StartUtc, Is.EqualTo(expected.Start));
            Assert.That(booking.EndUtc, Is.EqualTo(expected.End));
            Assert.That(booking.Status, Is.EqualTo(VisitorBookingStatus.Booked));
            Assert.That(booking.VisitorName, Is.EqualTo(_visitorName));
            Assert.That(booking.HostUserId, Is.Null);
        });
        await ShowOwnUpcomingBookingAsync();
        await Expect(Page.Locator(".visitors-desktop-list tr", new() { HasText = _visitorName }))
            .ToContainTextAsync("celý den");
    }

    [Test]
    public async Task Time_window_mode_explains_invalid_time_then_saves_the_selected_interval()
    {
        await OpenBookingAsync(ReservationTimeMode.TimeWindow);
        await Expect(Dialog.Locator("input[type=time]")).ToHaveCountAsync(2);
        // Native date min/max are guidance; a forged date must not crash the Blazor circuit.
        var date = Dialog.Locator("#visitors-date");
        await date.FillAsync("9999-12-31");
        await date.BlurAsync();
        await Expect(Dialog).ToContainTextAsync("Tento den je mimo povolený plánovací horizont.");
        await Expect(BookButton).ToBeDisabledAsync();
        await Expect(Dialog).Not.ToContainTextAsync("není volné žádné návštěvnické místo");
        await date.FillAsync(_day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await date.BlurAsync();
        await SetTimeAsync("visitors-from", "10:15");
        await SetTimeAsync("visitors-to", "09:00");
        await Expect(Dialog).ToContainTextAsync("Konec musí být po začátku.");
        await Expect(BookButton).ToBeDisabledAsync();
        await Expect(Dialog).Not.ToContainTextAsync("není volné žádné návštěvnické místo");

        await SetTimeAsync("visitors-to", "11:45");
        await Expect(Dialog).Not.ToContainTextAsync("Konec musí být po začátku.");
        await SelectOwnSpotAndNameAsync();
        await BookButton.ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0);
        await using var db = new D3ParkingDbContext(_options!);
        var booking = await db.VisitorBookings.SingleAsync(b => b.SpotId == _spotId);
        Assert.Multiple(() =>
        {
            Assert.That(booking.StartUtc, Is.EqualTo(SiteTime.At(_day, new TimeOnly(10, 15), _timeZone)));
            Assert.That(booking.EndUtc, Is.EqualTo(SiteTime.At(_day, new TimeOnly(11, 45), _timeZone)));
            Assert.That(booking.Status, Is.EqualTo(VisitorBookingStatus.Booked));
        });
        await ShowOwnUpcomingBookingAsync();
        var row = Page.Locator(".visitors-desktop-list tr", new() { HasText = _visitorName });
        await Expect(row).ToContainTextAsync("10:15");
        await Expect(row).ToContainTextAsync("11:45");
    }

    [Test]
    public async Task A_spot_taken_after_selection_reports_conflict_without_a_second_booking()
    {
        await OpenBookingAsync(ReservationTimeMode.TimeWindow);
        await SetTimeAsync("visitors-from", "10:15");
        await SetTimeAsync("visitors-to", "11:45");
        await SelectOwnSpotAndNameAsync();
        var competingName = $"{_visitorName} competing";
        await using (var db = new D3ParkingDbContext(_options!))
        {
            db.VisitorBookings.Add(new VisitorBooking(_spotId, competingName, null, null, null,
                SiteTime.At(_day, new TimeOnly(10, 30), _timeZone),
                SiteTime.At(_day, new TimeOnly(11, 0), _timeZone), _actorId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        await BookButton.ClickAsync();
        await Expect(Dialog).ToBeVisibleAsync();
        await Expect(Dialog).ToContainTextAsync(new Regex("(už|již) (obsazené|rezervované)"));
        await Expect(NameInput).ToHaveValueAsync(_visitorName);
        await using var check = new D3ParkingDbContext(_options!);
        var bookings = await check.VisitorBookings.Where(b => b.SpotId == _spotId).ToListAsync();
        Assert.That(bookings, Has.Count.EqualTo(1));
        Assert.That(bookings[0].VisitorName, Is.EqualTo(competingName));
    }

    [TestCase(ReservationTimeMode.AllDay, ReservationTimeMode.TimeWindow)]
    [TestCase(ReservationTimeMode.TimeWindow, ReservationTimeMode.AllDay)]
    public async Task A_changed_time_mode_preserves_the_draft_and_requires_resubmission_in_the_current_mode(
        ReservationTimeMode originalMode, ReservationTimeMode currentMode)
    {
        await OpenBookingAsync(originalMode);
        if (originalMode == ReservationTimeMode.TimeWindow)
        {
            await SetTimeAsync("visitors-from", "10:15");
            await SetTimeAsync("visitors-to", "11:45");
        }
        await SelectOwnSpotAndNameAsync();
        await SetModeAsync(currentMode);

        await BookButton.ClickAsync();
        await Expect(Dialog).ToContainTextAsync("Pravidlo času rezervace se mezitím změnilo");
        await Expect(Dialog.Locator("input[type=time]"))
            .ToHaveCountAsync(currentMode == ReservationTimeMode.AllDay ? 0 : 2);
        await Expect(NameInput).ToHaveValueAsync(_visitorName);
        await Expect(Dialog.Locator("#visitors-date"))
            .ToHaveValueAsync(_day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await Expect(Dialog.Locator("#visitors-spot")).ToHaveJSPropertyAsync("value", _spotId.ToString());
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.VisitorBookings.CountAsync(b => b.SpotId == _spotId), Is.Zero);

        if (currentMode == ReservationTimeMode.TimeWindow)
        {
            await SetTimeAsync("visitors-from", "10:15");
            await SetTimeAsync("visitors-to", "11:45");
        }
        await Expect(BookButton).ToBeEnabledAsync();
        await BookButton.ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0);
        var booking = await db.VisitorBookings.SingleAsync(b => b.SpotId == _spotId);
        var expected = currentMode == ReservationTimeMode.AllDay
            ? SiteTime.Day(_day, _timeZone)
            : (Start: SiteTime.At(_day, new TimeOnly(10, 15), _timeZone),
               End: SiteTime.At(_day, new TimeOnly(11, 45), _timeZone));
        Assert.Multiple(() =>
        {
            Assert.That(booking.StartUtc, Is.EqualTo(expected.Start));
            Assert.That(booking.EndUtc, Is.EqualTo(expected.End));
        });
    }

    [Test]
    public async Task A_host_departing_after_selection_preserves_the_draft_and_requires_explicit_resubmission()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var email = $"visitor-host-e2e-{suffix}@example.test";
        var host = new ApplicationUser
        {
            UserName = email, NormalizedUserName = email.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true,
            DisplayName = $"E2E Visitor Host {suffix}", Status = AccountStatus.Active,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
        try
        {
            await using (var seed = new D3ParkingDbContext(_options!))
            {
                seed.Users.Add(host);
                await seed.SaveChangesAsync();
            }

            // The departure preview must explain the confirmed host/creator rule before any
            // destructive action. Only open the preview for our synthetic account; never confirm.
            await Pages.GotoInteractiveAsync(Page, $"/admin/users/{host.Id}");
            await Page.Locator(".user-detail-tabs")
                .GetByRole(AriaRole.Button, new() { Name = "Přístupy", Exact = true }).ClickAsync();
            await Page.Locator(".user-delete-zone")
                .GetByRole(AriaRole.Button, new() { Name = "Smazat účet", Exact = true }).ClickAsync();
            var departureImpact = Page.GetByTestId("departure-visitors-impact");
            await Expect(departureImpact).ToContainTextAsync("včetně návštěv jiných hostitelů");
            await Expect(departureImpact).ToContainTextAsync("Skončené návštěvy zůstanou beze změny");
            await Expect(departureImpact).ToContainTextAsync("každé zrušení se zapíše do auditu");

            await OpenBookingAsync(ReservationTimeMode.AllDay);
            await SelectOwnSpotAndNameAsync();
            var hostChoice = Dialog.Locator("#visitors-host");
            await hostChoice.ClickAsync();
            await Page.GetByRole(AriaRole.Option, new() { Name = host.DisplayName, Exact = true }).ClickAsync();
            await Expect(hostChoice).ToHaveJSPropertyAsync("value", host.Id.ToString());

            // Reproduce departure after the open form has loaded its active host options. This
            // fixture mutation is restricted to the synthetic account and the guarded local DB.
            await using (var departed = new D3ParkingDbContext(_options!))
            {
                await departed.Users.Where(u => u.Id == host.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AccountStatus.Blocked));
            }

            await BookButton.ClickAsync();
            await Expect(Dialog).ToBeVisibleAsync();
            await Expect(Dialog).ToContainTextAsync("Vybraný hostitel již nemá aktivní účet.");
            await Expect(Dialog).Not.ToContainTextAsync("Parking_Visitor_Error_HostUnavailable");
            await Expect(NameInput).ToHaveValueAsync(_visitorName);
            await Expect(Dialog.Locator("#visitors-date"))
                .ToHaveValueAsync(_day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await Expect(Dialog.Locator("#visitors-spot")).ToHaveJSPropertyAsync("value", _spotId.ToString());
            await Expect(hostChoice).ToHaveJSPropertyAsync("value", string.Empty);
            await using (var rejected = new D3ParkingDbContext(_options!))
            {
                Assert.That(await rejected.VisitorBookings.CountAsync(b => b.SpotId == _spotId), Is.Zero,
                    "A departed host must not be silently discarded while saving the first request.");
            }

            await hostChoice.ClickAsync();
            await Expect(Page.GetByRole(AriaRole.Option, new() { Name = host.DisplayName, Exact = true })).ToHaveCountAsync(0);
            await Page.GetByRole(AriaRole.Option, new() { Name = "Bez hostitele (volitelné)", Exact = true }).ClickAsync();
            await Expect(BookButton).ToBeEnabledAsync();
            await BookButton.ClickAsync();
            await Expect(Dialog).ToHaveCountAsync(0);
            await Expect(Page.Locator(".visitors-page")).ToContainTextAsync("Návštěva má rezervované místo.");
            await using var verify = new D3ParkingDbContext(_options!);
            var booking = await verify.VisitorBookings.SingleAsync(b => b.SpotId == _spotId);
            Assert.Multiple(() =>
            {
                Assert.That(booking.HostUserId, Is.Null);
                Assert.That(booking.VisitorName, Is.EqualTo(_visitorName));
                Assert.That(booking.Status, Is.EqualTo(VisitorBookingStatus.Booked));
                Assert.That(booking.StartUtc, Is.EqualTo(SiteTime.Day(_day, _timeZone).Start));
                Assert.That(booking.EndUtc, Is.EqualTo(SiteTime.Day(_day, _timeZone).End));
            });
        }
        finally
        {
            await using var cleanup = new D3ParkingDbContext(_options!);
            await using var transaction = await cleanup.Database.BeginTransactionAsync();
            await DeleteOwnBookingsAndAuditsAsync(cleanup);
            await cleanup.NotificationEmailDeliveries.Where(n => n.UserId == host.Id).ExecuteDeleteAsync();
            await cleanup.Notifications.Where(n => n.UserId == host.Id).ExecuteDeleteAsync();
            await cleanup.AccountAuditEvents.Where(a => a.UserId == host.Id).ExecuteDeleteAsync();
            await cleanup.Users.Where(u => u.Id == host.Id).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
    }

    [Test]
    public async Task Cancellation_requires_confirmation_preserves_history_and_releases_the_interval()
    {
        var bookingId = await SeedOwnBookingAsync();
        await OpenCancellationAsync(bookingId);
        await Expect(CancelDialog).ToContainTextAsync(_visitorName);
        await Expect(CancelDialog).ToContainTextAsync(_spotCode);
        await Expect(CancelDialog).ToContainTextAsync("celý den");
        await Expect(CancelDialog).ToContainTextAsync("Zrušení uvolní tento rezervovaný termín");
        await Expect(CancelDialog).ToContainTextAsync("pokud je místo aktivní");
        await Expect(CancelDialog).ToContainTextAsync("Záznam zůstane zachován");

        // Opening the dialog must not mutate data. Dismissing it keeps the same active row.
        await AssertBookingStateAsync(bookingId, VisitorBookingStatus.Booked, expectedAuditCount: 0);
        await KeepBookingButton.ClickAsync();
        await Expect(CancelDialog).ToHaveCountAsync(0);
        await Expect(CancelBookingButton(bookingId)).ToBeVisibleAsync();
        await AssertBookingStateAsync(bookingId, VisitorBookingStatus.Booked, expectedAuditCount: 0);

        await CancelBookingButton(bookingId).ClickAsync();
        await Expect(CancelDialog).ToBeVisibleAsync();
        await ConfirmCancelButton.ClickAsync();
        await Expect(CancelDialog).ToHaveCountAsync(0);
        await Expect(Page.Locator(".visitors-page")).ToContainTextAsync("Rezervace návštěvy byla zrušena.");
        await Expect(CancelBookingButton(bookingId)).ToHaveCountAsync(0);
        await AssertBookingStateAsync(bookingId, VisitorBookingStatus.Cancelled, expectedAuditCount: 1);

        await using (var db = new D3ParkingDbContext(_options!))
        {
            var audit = await db.AccountAuditEvents.SingleAsync(a => a.Detail != null
                && a.Detail.Contains($"Visitor booking {bookingId}:"));
            Assert.Multiple(() =>
            {
                Assert.That(audit.Type, Is.EqualTo(AccountAuditEventType.ReservationOverridden));
                Assert.That(audit.UserId, Is.EqualTo(_actorId));
                Assert.That(audit.Actor, Is.EqualTo($"admin:{_actorId}"));
                Assert.That(audit.Detail, Does.Contain(_spotId.ToString()).And.Contain("cancelled"));
                Assert.That(audit.Detail, Does.Not.Contain(_visitorName));
            });
        }

        // Verify availability through the real form and a second server-side save, while the
        // cancelled original stays in SQL as history and no longer blocks the same interval.
        await OpenBookingAsync(ReservationTimeMode.AllDay);
        await SelectOwnSpotAndNameAsync();
        await BookButton.ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0);
        await using var check = new D3ParkingDbContext(_options!);
        var bookings = await check.VisitorBookings.Where(b => b.SpotId == _spotId).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(bookings, Has.Count.EqualTo(2));
            Assert.That(bookings.Single(b => b.Id == bookingId).Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
            Assert.That(bookings.Single(b => b.Id != bookingId).Status, Is.EqualTo(VisitorBookingStatus.Booked));
        });
    }

    [Test]
    public async Task A_cancelled_booking_changed_while_confirmation_is_open_reports_conflict_and_refreshes()
    {
        var bookingId = await SeedOwnBookingAsync();
        await OpenCancellationAsync(bookingId);
        // Deterministically reproduce the second receptionist's cancellation after this browser
        // read the Booked row. This fixture change is intentionally outside the UI transaction.
        await using (var db = new D3ParkingDbContext(_options!))
        {
            var booking = await db.VisitorBookings.SingleAsync(b => b.Id == bookingId);
            booking.Cancel();
            await db.SaveChangesAsync();
        }

        await ConfirmCancelButton.ClickAsync();
        await Expect(CancelDialog).ToHaveCountAsync(0);
        await Expect(Page.Locator(".visitors-page")).ToContainTextAsync(new Regex("(už|již).*zrušena", RegexOptions.IgnoreCase));
        await Expect(Page.Locator(".visitors-page")).Not.ToContainTextAsync("Rezervace návštěvy byla zrušena.");
        await Expect(CancelBookingButton(bookingId)).ToHaveCountAsync(0);
        await AssertBookingStateAsync(bookingId, VisitorBookingStatus.Cancelled, expectedAuditCount: 0);
    }

    private ILocator CancelBookingButton(Guid bookingId) =>
        Page.Locator($".visitors-desktop-list #visitors-cancel-{bookingId} button");

    private async Task<Guid> SeedOwnBookingAsync()
    {
        await SetModeAsync(ReservationTimeMode.AllDay);
        var window = SiteTime.Day(_day, _timeZone);
        var booking = new VisitorBooking(_spotId, _visitorName, null, null, null,
            window.Start, window.End, _actorId, DateTimeOffset.UtcNow);
        await using var db = new D3ParkingDbContext(_options!);
        db.VisitorBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task OpenCancellationAsync(Guid bookingId)
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/visitors");
        await ShowOwnUpcomingBookingAsync();
        await CancelBookingButton(bookingId).ClickAsync();
        await Expect(CancelDialog).ToBeVisibleAsync();
    }

    private async Task AssertBookingStateAsync(Guid bookingId, VisitorBookingStatus status, int expectedAuditCount)
    {
        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(db.VisitorBookings.Single(b => b.Id == bookingId).Status, Is.EqualTo(status));
            Assert.That(db.AccountAuditEvents.Count(a => a.Detail != null
                && a.Detail.Contains($"Visitor booking {bookingId}:")), Is.EqualTo(expectedAuditCount));
        });
    }

    private async Task DeleteOwnBookingsAndAuditsAsync(D3ParkingDbContext db)
    {
        var bookingIds = await db.VisitorBookings.Where(b => b.SpotId == _spotId).Select(b => b.Id).ToListAsync();
        foreach (var bookingId in bookingIds)
        {
            var prefix = $"Visitor booking {bookingId}:";
            await db.AccountAuditEvents.Where(a => a.Detail != null && a.Detail.StartsWith(prefix)).ExecuteDeleteAsync();
        }
        await db.VisitorBookings.Where(b => b.SpotId == _spotId).ExecuteDeleteAsync();
    }

    private async Task OpenBookingAsync(ReservationTimeMode mode)
    {
        await SetModeAsync(mode);
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/visitors");
        await Page.Locator("#visitors-open-create button").ClickAsync();
        await Expect(Dialog).ToBeVisibleAsync();
        var date = Dialog.Locator("#visitors-date");
        await date.FillAsync(_day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await date.BlurAsync();
        await Expect(date).ToHaveValueAsync(_day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private async Task SetModeAsync(ReservationTimeMode mode)
    {
        await using var db = new D3ParkingDbContext(_options!);
        await db.ParkingSettings.Where(s => s.Id == ParkingSettings.SingletonId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ReservationTimeMode, mode));
    }

    private async Task SetTimeAsync(string id, string value)
    {
        var input = Dialog.Locator($"#{id}");
        await input.FillAsync(value);
        await input.BlurAsync();
        await Expect(input).ToHaveValueAsync(value);
    }

    private async Task SelectOwnSpotAndNameAsync()
    {
        var spot = Dialog.Locator("#visitors-spot");
        await Expect(spot).ToBeEnabledAsync();
        await spot.ClickAsync();
        await Page.GetByRole(AriaRole.Option, new() { Name = _spotCode, Exact = true }).ClickAsync();
        await NameInput.FillAsync(_visitorName);
        await NameInput.BlurAsync();
        await Expect(BookButton).ToBeEnabledAsync();
    }

    private async Task ShowOwnUpcomingBookingAsync()
    {
        await Page.Locator(".visitors-view-switch button", new() { HasText = "Nadcházející návštěvy" })
            .ClickAsync();
        await Expect(Page.Locator(".visitors-desktop-list tr", new() { HasText = _visitorName }))
            .ToBeVisibleAsync();
    }
}
