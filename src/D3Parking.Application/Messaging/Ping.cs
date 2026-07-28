namespace D3Parking.Application.Messaging;

// Smoke-test message to verify the Wolverine pipeline is wired. Replace with real messages.
public record Ping(string Source);

public record Pong(string Source, DateTimeOffset HandledAt);

public static class PingHandler
{
    public static Pong Handle(Ping command) => new(command.Source, DateTimeOffset.UtcNow);
}
