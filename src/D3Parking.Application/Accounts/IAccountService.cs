namespace D3Parking.Application.Accounts;

/// <summary>
/// Account lifecycle and credential operations. Self-service callers act on their own account;
/// administrative operations carry the acting admin's id. Every operation records an audit event.
/// </summary>
public interface IAccountService
{
    // Registration.
    Task<AccountResult> RegisterAsync(string email, string password, string? displayName, string? licensePlate = null, CancellationToken cancellationToken = default);

    // Activation (email confirmation).
    Task<AccountResult> SendActivationEmailAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<AccountResult> ConfirmEmailAsync(Guid userId, string token, CancellationToken cancellationToken = default);

    // Password.
    Task<AccountResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
    Task<AccountResult> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);
    Task<AccountResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default);

    // Email change (confirmed on the new address).
    Task<AccountResult> RequestEmailChangeAsync(Guid userId, string newEmail, CancellationToken cancellationToken = default);
    Task<AccountResult> ConfirmEmailChangeAsync(Guid userId, string newEmail, string token, CancellationToken cancellationToken = default);

    // Phone change (verification code delivered by email).
    Task<AccountResult> RequestPhoneChangeAsync(Guid userId, string newPhoneNumber, CancellationToken cancellationToken = default);
    Task<AccountResult> ConfirmPhoneChangeAsync(Guid userId, string newPhoneNumber, string code, CancellationToken cancellationToken = default);

    // Self-service lifecycle.
    Task<AccountResult> DeactivateAsync(Guid userId, string? reason, CancellationToken cancellationToken = default);
    Task<AccountResult> ReactivateAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<AccountResult> RequestReactivationAsync(string email, CancellationToken cancellationToken = default);
    Task<AccountResult> ConfirmReactivationAsync(Guid userId, string token, CancellationToken cancellationToken = default);
    Task<AccountResult> RequestSuspendAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<AccountResult> ConfirmSuspendAsync(Guid userId, string token, CancellationToken cancellationToken = default);

    // Administrative lifecycle.
    Task<AccountResult> BlockAsync(Guid userId, Guid adminId, string? reason, CancellationToken cancellationToken = default);
    Task<AccountResult> UnblockAsync(Guid userId, Guid adminId, CancellationToken cancellationToken = default);

    // Audit.
    Task<IReadOnlyList<AccountAuditEntry>> GetAuditTrailAsync(Guid userId, CancellationToken cancellationToken = default);
}
