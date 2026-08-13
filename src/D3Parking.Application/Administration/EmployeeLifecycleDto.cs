namespace D3Parking.Application.Administration;

/// <summary>
/// The exact operational footprint an administrator is about to remove. Historical records are
/// counted separately because they survive without the account's identity.
/// </summary>
public sealed record EmployeeDeletionImpact(
    int OwnedSpots,
    int PairedVehicles,
    int ActiveReservations,
    int ActiveQueueEntries,
    int UpcomingVisitorBookings,
    int PersonalMessages,
    int AssignedOversightCases,
    int HistoricalRecords)
{
    public int OperationalChanges => OwnedSpots + PairedVehicles + ActiveReservations
        + ActiveQueueEntries + UpcomingVisitorBookings + PersonalMessages + AssignedOversightCases;
}
