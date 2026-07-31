using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>Administration of the physical parking spots that make up the lot.</summary>
public interface IParkingSpotService
{
    Task<IReadOnlyList<ParkingSpotDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the spot catalogue in natural code order, for the administration screen. Inactive
    /// spots are included — reactivating one is exactly what that screen is for.
    /// </summary>
    /// <param name="search">Substring of the code; null or blank lists the whole lot.</param>
    /// <param name="jumpToCode">
    /// When set and present in the result set, the page holding that code is returned instead of
    /// <paramref name="pageIndex"/> — a spot just created lands wherever its code sorts, and a batch
    /// the admin cannot see is indistinguishable from one that was never created.
    /// </param>
    Task<PagedResult<ParkingSpotDto>> ListPageAsync(
        int pageIndex,
        int pageSize,
        string? search = null,
        string? jumpToCode = null,
        CancellationToken cancellationToken = default);

    Task<ParkingSpotDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ParkingResult> CreateAsync(string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Dry-run for a batch of codes: classifies each as new, duplicate of an existing spot, or invalid.</summary>
    Task<SpotBatchPlan> PreviewBatchAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates every new code of the batch with the given type in one transaction; codes that
    /// already exist are skipped and reported so re-running a series stays idempotent.
    /// </summary>
    Task<SpotBatchResult> CreateBatchAsync(IReadOnlyList<string> codes, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default);

    Task<ParkingResult> UpdateAsync(Guid id, string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default);

    Task<ParkingResult> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default);

    /// <summary>Assigns a resident to the spot, or clears ownership when ownerId is null.</summary>
    Task<ParkingResult> AssignOwnerAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recorded "could not park at the reserved spot" events, newest first — the admin trend view
    /// behind the driver-facing "I can't park" flow, including each report's photo proof flag and
    /// the review state of the apology voucher it granted.
    /// </summary>
    Task<IReadOnlyList<OccupancyMismatchDto>> GetOccupancyMismatchesAsync(int take = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// One report, with the same evidence the trend view resolves — the panel an oversight case
    /// opened for a mismatch reads. Null when the report is gone.
    /// </summary>
    Task<OccupancyMismatchDto?> GetOccupancyMismatchAsync(Guid mismatchId, CancellationToken cancellationToken = default);

    /// <summary>The reporter's photo proof for a mismatch, or null when the record has none (pre-photo era).</summary>
    Task<MismatchPhotoDto?> GetMismatchPhotoAsync(Guid mismatchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The spot manager confirms the blocked-spot report: the pending voucher becomes redeemable
    /// (validity restarts from the approval) and the holder is notified.
    /// </summary>
    Task<ParkingResult> ApproveVoucherAsync(Guid voucherId, Guid reviewerId, CancellationToken cancellationToken = default);

    /// <summary>The spot manager judges the report unfounded: the pending voucher is voided and the holder notified.</summary>
    Task<ParkingResult> RejectVoucherAsync(Guid voucherId, Guid reviewerId, CancellationToken cancellationToken = default);
}

/// <summary>A mismatch report's photo proof, streamed to the spot manager's review.</summary>
public sealed record MismatchPhotoDto(byte[] Content, string ContentType);
