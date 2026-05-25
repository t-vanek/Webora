namespace Webora.Domain.Accounts;

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
    PasswordChanged,
    PasswordResetRequested,
    PasswordReset,
    EmailChangeRequested,
    EmailChanged,
    PhoneChangeRequested,
    PhoneChanged,
}
