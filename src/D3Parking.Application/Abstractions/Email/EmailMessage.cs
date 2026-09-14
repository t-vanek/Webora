namespace D3Parking.Application.Abstractions.Email;

public sealed record EmailMessage
{
    /// <summary>Stable SMTP identity across durable-outbox retries.</summary>
    public string? MessageId { get; init; }

    public required string To { get; init; }

    public string? ToName { get; init; }

    public required string Subject { get; init; }

    public required string HtmlBody { get; init; }

    public string? TextBody { get; init; }
}
