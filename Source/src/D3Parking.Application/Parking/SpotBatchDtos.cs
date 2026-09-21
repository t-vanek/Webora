namespace D3Parking.Application.Parking;

/// <summary>Dry-run of a spot batch: how the requested codes split against the current lot.</summary>
public sealed record SpotBatchPlan(
    IReadOnlyList<string> NewCodes,
    IReadOnlyList<string> DuplicateCodes,
    IReadOnlyList<string> InvalidCodes);

/// <summary>
/// Outcome of creating a spot batch. Unlike <see cref="ParkingResult"/>, success has structure:
/// how many spots were created and which codes were skipped because they already existed.
/// </summary>
public sealed record SpotBatchResult(
    bool Succeeded,
    int CreatedCount,
    IReadOnlyList<string> SkippedCodes,
    IReadOnlyList<string> Errors)
{
    public static SpotBatchResult Success(int created, IReadOnlyList<string> skipped) => new(true, created, skipped, []);

    public static SpotBatchResult Failure(params string[] errors) => new(false, 0, [], errors);
}
