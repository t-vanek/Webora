namespace Webora.Domain.Accounts;

public enum AccountAuditEventType
{
    Registered,
    ActivationRequested,
    Activated,
    Deactivated,
    Reactivated,
    SuspendRequested,
    Suspended,
    Blocked,
    Unblocked,
    PasswordChanged,
    PasswordResetRequested,
    PasswordReset,
    EmailChangeRequested,
    EmailChanged,
    PhoneChangeRequested,
    PhoneChanged,
}
