namespace D3Parking.Domain.Accounts;

public enum AccountAuditEventType
{
    Registered,
    /// <summary>Someone tried to register with this account's email; the owner was notified.</summary>
    RegistrationRepeated,
    ActivationRequested,
    Activated,
    Deactivated,
    ReactivationRequested,
    Reactivated,
    SuspendRequested,
    Suspended,
    Blocked,
    Unblocked,
    RolesChanged,
    /// <summary>This account changed which permissions a role grants, or created/deleted a role.</summary>
    RolePermissionsChanged,
    SettingsChanged,
    /// <summary>A spot manager cancelled or moved one of this account's parking reservations.</summary>
    ReservationOverridden,
    /// <summary>An oversight case ended in a warning or a deduction against this account.</summary>
    OversightSanctioned,
    PasswordChanged,
    PasswordResetRequested,
    PasswordReset,
    EmailChangeRequested,
    EmailChanged,
    PhoneChangeRequested,
    PhoneChanged,
}
