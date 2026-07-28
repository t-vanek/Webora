using Wolverine;

namespace D3Parking.Application.Abstractions.Email;

/// <summary>
/// Hands email off to a Wolverine local queue instead of blocking on the SMTP round-trip, so an
/// unreachable mail server no longer fails the request that triggered the mail (registration,
/// password reset, admin actions). Delivery and its retries happen in <see cref="EmailHandler"/>.
/// </summary>
/// <remarks>
/// The local queue is in-memory: messages still waiting when the process stops are lost. That is an
/// accepted trade-off here because every mail can be requested again (activation resend, password
/// reset). Configure Wolverine message persistence if a mail must survive a restart.
/// </remarks>
public sealed class QueuedEmailSender(IMessageBus bus) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        bus.PublishAsync(message).AsTask();
}
