using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>
/// Validates the planner settings before the domain's defensive normalization is applied.
/// The returned key is shared by the admin UI and service boundary.
/// </summary>
public static class ParkingSettingsValidator
{
    public const string CalendarError = "Parking_Settings_CalendarInvalid";
    public const string WeekdaysError = "Parking_Settings_AllowedWeekdaysRequired";
    public const string WeeklyLimitError = "Parking_Settings_WeeklyLimitInvalid";
    public const string QueueError = "Parking_Settings_QueueOfferInvalid";
    public const string OccupancyThresholdError = "Parking_Settings_OccupancyThresholdsInvalid";
    public const string OccupancyWindowError = "Parking_Settings_OccupancyWindowInvalid";
    public const string OccupancyHourError = "Parking_Settings_OccupancySendHourInvalid";
    public const string BudgetError = "Parking_Settings_BudgetInvalid";
    public const string BudgetAllowanceError = "Parking_Settings_BudgetAllowanceInvalid";
    public const string OversightSlaError = "Parking_Settings_OversightSlaInvalid";
    public const string OversightRecurrenceError = "Parking_Settings_OversightRecurrenceInvalid";
    public const string OversightScheduleError = "Parking_Settings_OversightScheduleInvalid";
    public const string ResidentPolicyError = "Parking_Settings_ResidentPolicyInvalid";
    public const string ResidentDeadlineError = "Parking_Settings_ResidentDeadlineInvalid";
    public const string ResidentPlanError = "Parking_Settings_ResidentPlanInvalid";
    public const string CoordinatesError = "Parking_Settings_CoordinatesInvalid";
    public const string LocationLimitsError = "Parking_Settings_LocationLimitsInvalid";

    public static string? Validate(ParkingSettingsDto settings)
    {
        if (!Enum.IsDefined(settings.ReservationTimeMode)
            || settings.ReservationHorizonDays is < 1 or > 366
            || !Enum.IsDefined(settings.HolidayCalendarRegion))
        {
            return CalendarError;
        }

        var allowedWeekdays = settings.AllowedReservationWeekdays.Sanitize();
        if (allowedWeekdays is Weekday.None || allowedWeekdays != settings.AllowedReservationWeekdays)
        {
            return WeekdaysError;
        }

        if (settings.WeeklyReservationLimitEnabled
            && (settings.WeeklyReservationLimit < 1
                || settings.WeeklyReservationLimit > allowedWeekdays.CountDays()))
        {
            return WeeklyLimitError;
        }

        if (settings.QueueOfferMinutes < 1)
        {
            return QueueError;
        }

        if (settings.AvailabilityFreeThresholdPercent is < 1 or > 99
            || settings.AvailabilityBusyThresholdPercent is < 2 or > 100
            || settings.AvailabilityFreeThresholdPercent >= settings.AvailabilityBusyThresholdPercent)
        {
            return OccupancyThresholdError;
        }

        var maximumLookahead = Math.Min(60, settings.ReservationHorizonDays);
        if (settings.AvailabilityLookaheadDays < 1
            || settings.AvailabilityLookaheadDays > maximumLookahead
            || settings.AvailabilityMinConsecutiveDays < 1
            || settings.AvailabilityMinConsecutiveDays > settings.AvailabilityLookaheadDays)
        {
            return OccupancyWindowError;
        }

        if (settings.AvailabilitySendHourLocal is < 0 or > 23)
        {
            return OccupancyHourError;
        }

        if (settings.BaseReservationCost < 0
            || settings.MonthlyCreditAllowance < 0
            || !Enum.IsDefined(settings.BudgetRenewalPeriod))
        {
            return BudgetError;
        }

        if (settings.BaseReservationCost > 0
            && settings.MonthlyCreditAllowance < settings.BaseReservationCost)
        {
            return BudgetAllowanceError;
        }

        if (settings.OversightSlaCriticalHours < 1
            || settings.OversightSlaHighHours < settings.OversightSlaCriticalHours
            || settings.OversightSlaNormalHours < settings.OversightSlaHighHours
            || settings.OversightSlaLowHours < settings.OversightSlaNormalHours)
        {
            return OversightSlaError;
        }

        if (settings.OversightRecurrenceThreshold < 2
            || settings.OversightRecurrenceWindowDays is < 1 or > 365)
        {
            return OversightRecurrenceError;
        }

        if (settings.OversightDigestHourLocal is < 0 or > 23
            || settings.OversightInfoDeadlineDays is < 1 or > 90
            || settings.OversightDisputeWindowDays is < 1 or > 365)
        {
            return OversightScheduleError;
        }

        if (!Enum.IsDefined(settings.ResidentReclaimPolicy)
            || !Enum.IsDefined(settings.ResidentProtectionDeadlineMode)
            || !Enum.IsDefined(settings.ResidentNoReplacementAction)
            || !Enum.IsDefined(settings.ResidentAlternativeBookingPolicy))
        {
            return ResidentPolicyError;
        }

        if (settings.ResidentPlanHorizonDays is < 1 or > 366
            || settings.ResidentPlanHorizonDays > settings.ReservationHorizonDays)
        {
            return ResidentPlanError;
        }

        var policyUsesDeadline = settings.ResidentReclaimPolicy is
            ResidentReclaimPolicy.AdvancePriority or ResidentReclaimPolicy.AdvanceOrReplacement;
        if (policyUsesDeadline
            && settings.ResidentProtectionDeadlineMode == ResidentProtectionDeadlineMode.HoursBeforeStart
            && settings.ResidentProtectionLeadHours is < 1 or > 168)
        {
            return ResidentDeadlineError;
        }

        if (settings.LotLatitude.HasValue != settings.LotLongitude.HasValue
            || settings.LotLatitude is { } latitude
                && (!double.IsFinite(latitude) || latitude is < -90 or > 90)
            || settings.LotLongitude is { } longitude
                && (!double.IsFinite(longitude) || longitude is < -180 or > 180))
        {
            return CoordinatesError;
        }

        if (settings.AutoVerifyMaxDistanceKm < 0
            || settings.MaxRewardedReleasesPerDay < 0
            || settings.MaxReleaseRangeDays is < 1 or > 366)
        {
            return LocationLimitsError;
        }

        return null;
    }
}
