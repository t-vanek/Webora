namespace D3Parking.Domain.Accounts;

public enum AccountStatus
{
    /// <summary>Registered but the email is not yet confirmed; cannot sign in.</summary>
    PendingActivation,

    Active,

    /// <summary>Voluntarily disabled by the user; reversible by the user.</summary>
    Deactivated,

    /// <summary>Temporarily paused by the user (confirmed by email); reversible.</summary>
    Suspended,

    /// <summary>Blocked by an administrator; reversible only by an administrator.</summary>
    Blocked,
}
