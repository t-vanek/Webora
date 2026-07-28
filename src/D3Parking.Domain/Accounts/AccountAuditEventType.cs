namespace D3Parking.Domain.Accounts;

public enum AccountAuditEventType
{
    Registered,
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
    SettingsChanged,
    PasswordChanged,
    PasswordResetRequested,
    PasswordReset,
    EmailChangeRequested,
    EmailChanged,
    PhoneChangeRequested,
    PhoneChanged,
}
