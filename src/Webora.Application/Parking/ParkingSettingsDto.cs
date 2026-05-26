namespace Webora.Application.Parking;

/// <summary>The editable parking and incentive settings.</summary>
public sealed record ParkingSettingsDto(
    int ReleasePoints,
    int OffPeakBonusPoints,
    int NoShowPenaltyPoints,
    TimeSpan ReleaseCutoff,
    TimeSpan NoShowGracePeriod,
    TimeSpan ReminderLeadTime,
    TimeOnly PeakStart,
    TimeOnly PeakEnd,
    TimeSpan SweepInterval,
    TimeOnly ResidentHoldUntil,
    int ResidentReleasePointsPerHour,
    int ResidentReleaseMaxPoints,
    int ResidentMaxShareAllowance,
    int ResidentSharePercentPerAllowance,
    int ResidentWastedShareClawbackPercent);
