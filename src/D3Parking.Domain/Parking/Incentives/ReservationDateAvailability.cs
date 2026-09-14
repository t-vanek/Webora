namespace D3Parking.Domain.Parking.Incentives;

/// <summary>
/// The single, role-independent decision describing whether configuration permits a new
/// reservation on a local calendar date. A resident assignment is an entitlement to the spot,
/// not an exception to these booking rules.
/// </summary>
public enum ReservationDateAvailability
{
    Allowed,
    Past,
    SameDayNotAllowed,
    OutsideReservationHorizon,
    WeekdayNotAllowed,
    WeekendNotAllowed,
    PublicHolidayNotAllowed,
}
