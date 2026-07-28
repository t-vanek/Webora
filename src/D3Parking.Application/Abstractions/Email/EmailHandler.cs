namespace D3Parking.Application.Abstractions.Email;

/// <summary>
/// Delivers queued email through the configured transport. Runs on a Wolverine local queue, so a
/// transport failure is retried by the messaging policies rather than surfacing to the user.
/// </summary>
public static class EmailHandler
{
    public static Task Handle(EmailMessage message, IEmailTransport transport, CancellationToken cancellationToken) =>
        transport.SendAsync(message, cancellationToken);
}
