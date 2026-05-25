namespace Webora.Application.Abstractions.Email;

public sealed record EmailMessage
{
    public required string To { get; init; }

    public string? ToName { get; init; }

    public required string Subject { get; init; }

    public required string HtmlBody { get; init; }

    public string? TextBody { get; init; }
}
