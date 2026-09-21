namespace D3Parking.Application.Abstractions.Email;

/// <summary>
/// The actual delivery mechanism (SMTP), implemented in the infrastructure layer. Most application
/// code sends through <see cref="IEmailSender"/>; durable outboxes may call the transport directly
/// because they already own persistence and retry semantics.
/// </summary>
public interface IEmailTransport
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
