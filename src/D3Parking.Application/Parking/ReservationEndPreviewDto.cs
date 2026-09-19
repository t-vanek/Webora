namespace D3Parking.Application.Parking;

public sealed record ReservationEndPreviewDto(bool Started, int RefundCredits, bool RestoreVoucher,
    DateTimeOffset RefundDeadlineUtc, bool UsesBudget, bool CountsTowardWeeklyLimit);
