namespace D3Parking.Infrastructure.Parking;

/// <summary>One directed resident-to-guest interaction edge, already counted by SQL.</summary>
internal sealed record CompletedSharePair(Guid Owner, Guid Guest, int Count);
