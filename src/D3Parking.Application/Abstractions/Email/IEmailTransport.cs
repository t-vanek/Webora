namespace D3Parking.Application.Abstractions.Email;

/// <summary>
/// The actual delivery mechanism (SMTP), implemented in the infrastructure layer. Application code
/// never calls this directly — it sends through <see cref="IEmailSender"/>, which queues the
/// message; only the queue handler reaches the transport.
/// </summary>
public interface IEmailTransport
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
