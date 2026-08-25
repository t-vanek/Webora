using D3Parking.Domain.Parking.Incentives;

namespace D3Parking.Infrastructure.Parking;

internal static class ReservationDateAvailabilityExtensions
{
    public static string? ToParkingErrorKey(this ReservationDateAvailability availability) => availability switch
    {
        ReservationDateAvailability.Allowed => null,
        ReservationDateAvailability.SameDayNotAllowed => "Parking_Error_SameDayReservationsNotAllowed",
        ReservationDateAvailability.WeekdayNotAllowed or ReservationDateAvailability.WeekendNotAllowed =>
            "Parking_Error_ReservationWeekdayNotAllowed",
        ReservationDateAvailability.PublicHolidayNotAllowed => "Parking_Error_PublicHolidayNotAllowed",
        _ => "Parking_Error_ReservationHorizon",
    };
}
