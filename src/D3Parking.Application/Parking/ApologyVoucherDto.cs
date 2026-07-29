namespace D3Parking.Application.Parking;

/// <summary>The caller's usable apology voucher (one free reservation), if any.</summary>
public sealed record ApologyVoucherDto(Guid Id, DateTimeOffset ExpiresAtUtc);
