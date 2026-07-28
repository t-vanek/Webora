namespace D3Parking.Domain.Authorization;

public static class D3ParkingClaimTypes
{
    /// <summary>
    /// Claim type carrying a single fine-grained permission (e.g. "Pages.Edit").
    /// </summary>
    /// <remarks>
    /// The value deliberately keeps the old "webora:" prefix from before the rename to D3Parking.
    /// It is baked into every authentication cookie and OpenIddict token already issued — renaming
    /// it would make those tokens fail every permission check until each user signed in again.
    /// Change it only together with a forced sign-out of all sessions.
    /// </remarks>
    public const string Permission = "webora:permission";
}
