namespace D3Parking.Application.Parking;

/// <summary>A live price quote plus the user's independent planning-wallet balance.</summary>
public sealed record ReservationQuoteDto(
    int Cost,
    int OccupancyPercent,
    bool IsPeak,
    int Balance,
    bool Affordable,
    bool AutomaticCompensationAvailable);
