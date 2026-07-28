namespace D3Parking.Application.Abstractions.Email;

/// <summary>Transport-agnostic email sending. Implemented in the infrastructure layer (SMTP).</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
