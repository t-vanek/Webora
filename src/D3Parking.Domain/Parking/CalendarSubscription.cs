using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// Revocable private calendar feed for one user. Only a SHA-256 hash is persisted; possession of
/// the raw token is the credential for the read-only feed.
/// </summary>
public sealed class CalendarSubscription : Entity
{
    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private CalendarSubscription() { }

    public CalendarSubscription(Guid userId, string tokenHash, DateTimeOffset at)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(tokenHash)) throw new ArgumentException("A token hash is required.", nameof(tokenHash));

        UserId = userId;
        TokenHash = tokenHash;
        CreatedAtUtc = at;
        UpdatedAtUtc = at;
    }

    public void Rotate(string tokenHash, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(tokenHash)) throw new ArgumentException("A token hash is required.", nameof(tokenHash));
        TokenHash = tokenHash;
        UpdatedAtUtc = at;
    }
}
