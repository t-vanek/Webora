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
    SettingsChanged,
    PasswordChanged,
    PasswordResetRequested,
    PasswordReset,
    EmailChangeRequested,
    EmailChanged,
    PhoneChangeRequested,
    PhoneChanged,
}
