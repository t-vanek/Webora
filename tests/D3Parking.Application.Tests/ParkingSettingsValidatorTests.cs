using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public sealed class ParkingSettingsValidatorTests
{
    [Test]
    public void Valid_settings_are_accepted() =>
        Assert.That(ParkingSettingsValidator.Validate(ValidSettings()), Is.Null);

    [Test]
    public void Weekly_limit_must_fit_bookable_weekdays()
    {
        var settings = ValidSettings() with
        {
            AllowedReservationWeekdays = Weekday.Workdays,
            WeeklyReservationLimitEnabled = true,
            WeeklyReservationLimit = 6,
        };

        Assert.That(ParkingSettingsValidator.Validate(settings),
            Is.EqualTo(ParkingSettingsValidator.WeeklyLimitError));
    }

    [Test]
    public void Budget_must_cover_at_least_one_reservation()
    {
        var settings = ValidSettings() with
        {
            BaseReservationCost = 20,
            MonthlyCreditAllowance = 10,
        };

        Assert.That(ParkingSettingsValidator.Validate(settings),
            Is.EqualTo(ParkingSettingsValidator.BudgetAllowanceError));
    }

    [Test]
    public void Occupancy_thresholds_cannot_overlap()
    {
        var settings = ValidSettings() with
        {
            AvailabilityFreeThresholdPercent = 85,
            AvailabilityBusyThresholdPercent = 85,
        };

        Assert.That(ParkingSettingsValidator.Validate(settings),
            Is.EqualTo(ParkingSettingsValidator.OccupancyThresholdError));
    }

    [Test]
    public void Oversight_slas_must_stay_ordered()
    {
        var settings = ValidSettings() with
        {
            OversightSlaCriticalHours = 24,
            OversightSlaHighHours = 4,
        };

        Assert.That(ParkingSettingsValidator.Validate(settings),
            Is.EqualTo(ParkingSettingsValidator.OversightSlaError));
    }

    [Test]
    public void Coordinates_must_be_complete_and_in_range()
    {
        var incomplete = ValidSettings() with { LotLatitude = 50.08 };
        var outsideRange = ValidSettings() with { LotLatitude = 95, LotLongitude = 14.42 };

        Assert.Multiple(() =>
        {
            Assert.That(ParkingSettingsValidator.Validate(incomplete),
                Is.EqualTo(ParkingSettingsValidator.CoordinatesError));
            Assert.That(ParkingSettingsValidator.Validate(outsideRange),
                Is.EqualTo(ParkingSettingsValidator.CoordinatesError));
        });
    }

    [Test]
    public void Complete_notification_matrix_is_accepted()
    {
        var rules = ValidNotificationRules();

        Assert.That(NotificationDeliveryRuleValidator.Validate(rules), Is.Null);
    }

    [Test]
    public void Notification_matrix_rejects_live_delivery_without_inbox()
    {
        var rules = ValidNotificationRules();
        var index = rules.FindIndex(r => r.Level == NotificationLevel.Info);
        rules[index] = rules[index] with { InboxEnabled = false, LiveEnabled = true };

        Assert.That(NotificationDeliveryRuleValidator.Validate(rules),
            Is.EqualTo(NotificationDeliveryRuleValidator.Error));
    }

    private static List<NotificationDeliveryRuleDto> ValidNotificationRules() =>
        (from category in Enum.GetValues<NotificationCategory>()
         from level in Enum.GetValues<NotificationLevel>()
         let mandatory = level is NotificationLevel.Security or NotificationLevel.Critical
         select new NotificationDeliveryRuleDto(
             category, level, true, true, NotificationEmailMode.WhenRequested, mandatory))
        .ToList();

    private static ParkingSettingsDto ValidSettings()
    {
        var constructor = typeof(ParkingSettingsDto).GetConstructors().Single();
        var defaults = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null)
            .ToArray();
        var settings = (ParkingSettingsDto)constructor.Invoke(defaults);

        return settings with
        {
            ReservationTimeMode = ReservationTimeMode.AllDay,
            ReservationHorizonDays = 14,
            ResidentPlanHorizonDays = 14,
            AllowedReservationWeekdays = Weekday.Workdays,
            WeeklyReservationLimitEnabled = true,
            WeeklyReservationLimit = 2,
            HolidayCalendarRegion = HolidayCalendarRegion.CzechRepublic,
            QueueOfferMinutes = 30,
            AvailabilityLookaheadDays = 14,
            AvailabilityFreeThresholdPercent = 60,
            AvailabilityBusyThresholdPercent = 85,
            AvailabilityMinConsecutiveDays = 1,
            AvailabilitySendHourLocal = 9,
            BudgetRenewalPeriod = BudgetRenewalPeriod.Monthly,
            OversightSlaCriticalHours = 4,
            OversightSlaHighHours = 24,
            OversightSlaNormalHours = 72,
            OversightSlaLowHours = 168,
            OversightRecurrenceWindowDays = 30,
            OversightRecurrenceThreshold = 3,
            OversightDigestHourLocal = 8,
            OversightInfoDeadlineDays = 10,
            OversightDisputeWindowDays = 14,
            ResidentReclaimPolicy = ResidentReclaimPolicy.ConfirmedBookingProtected,
            ResidentProtectionDeadlineMode = ResidentProtectionDeadlineMode.HoursBeforeStart,
            ResidentProtectionLeadHours = 24,
            ResidentNoReplacementAction = ResidentNoReplacementAction.Deny,
            ResidentAlternativeBookingPolicy = ResidentAlternativeBookingPolicy.AutoRelease,
            MaxReleaseRangeDays = 92,
        };
    }
}
