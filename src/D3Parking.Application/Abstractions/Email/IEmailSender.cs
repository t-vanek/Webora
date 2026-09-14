namespace D3Parking.Application.Abstractions.Email;

/// <summary>Persists email for asynchronous delivery. Success means committed to SQL, not delivered by SMTP.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
