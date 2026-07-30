using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>
/// The caller's live apology voucher, if any: an approved one is redeemable (one free
/// reservation); a pending one is shown as "awaiting the spot manager's review".
/// </summary>
public sealed record ApologyVoucherDto(Guid Id, ApologyVoucherStatus Status, DateTimeOffset ExpiresAtUtc);
