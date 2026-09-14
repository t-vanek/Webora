using System.Text.Json;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Domain.Email;
using D3Parking.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace D3Parking.Infrastructure.Email;

public sealed class EmailDeliveryDispatcher(
    IDbContextFactory<D3ParkingDbContext> factory,
    IDataProtectionProvider protection,
    IEmailTransport transport,
    TimeProvider clock,
    ILogger<EmailDeliveryDispatcher> logger)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(2);

    public async Task DeliverAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var lease = Guid.NewGuid();
        var claimed = await db.EmailDeliveries.Where(d => d.Id == id
                && d.Status == EmailDeliveryStatus.Pending && d.NextAttemptUtc <= now
                && d.ExpiresAtUtc > now && d.Attempts < MaxAttempts
                && (d.LeasedUntilUtc == null || d.LeasedUntilUtc <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LeaseId, lease)
                .SetProperty(d => d.LeasedUntilUtc, now.Add(LeaseDuration))
                .SetProperty(d => d.Attempts, d => d.Attempts + 1), cancellationToken);
        if (claimed == 0) return;
        var delivery = await db.EmailDeliveries.AsNoTracking().SingleAsync(d => d.Id == id, cancellationToken);
        string? error = null;
        try
        {
            var json = protection.CreateProtector(DurableEmailSender.ProtectionPurpose).Unprotect(delivery.ProtectedPayload!);
            var message = JsonSerializer.Deserialize<EmailMessage>(json) ?? throw new JsonException("Empty envelope");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SendTimeout);
            await transport.SendAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Leave the committed lease recoverable; acceptance by SMTP may be unknown.
            throw;
        }
        catch (Exception ex)
        {
            // Provider exception messages may contain addresses, credentials or recovery URLs.
            error = ex.GetType().Name;
        }

        var completed = clock.GetUtcNow();
        var terminal = error is null || delivery.Attempts >= MaxAttempts || delivery.ExpiresAtUtc <= completed;
        var status = error is null ? EmailDeliveryStatus.Sent : terminal ? EmailDeliveryStatus.Failed : EmailDeliveryStatus.Pending;
        var delay = delivery.Attempts switch { 1 => 1, 2 => 5, 3 => 30, _ => 120 };
        // Fence the completion with the unique lease: a late worker must not overwrite a newer claim.
        var updated = await db.EmailDeliveries.Where(d => d.Id == id && d.LeaseId == lease)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status)
                .SetProperty(d => d.ProtectedPayload, terminal ? null : delivery.ProtectedPayload)
                .SetProperty(d => d.LastError, error)
                .SetProperty(d => d.CompletedAtUtc, terminal ? completed : (DateTimeOffset?)null)
                .SetProperty(d => d.NextAttemptUtc, completed.AddMinutes(delay))
                .SetProperty(d => d.LeaseId, (Guid?)null)
                .SetProperty(d => d.LeasedUntilUtc, (DateTimeOffset?)null), cancellationToken);
        if (updated == 0) logger.LogWarning("Email delivery {DeliveryId} lost its lease before completion.", id);
        else if (status == EmailDeliveryStatus.Failed)
            logger.LogError("Email delivery {DeliveryId} permanently failed after {Attempts} attempts ({ErrorType}).", id, delivery.Attempts, error);
        else if (error is not null)
            logger.LogWarning("Email delivery {DeliveryId} will retry after attempt {Attempts} ({ErrorType}).", id, delivery.Attempts, error);
    }

    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var exhausted = await db.EmailDeliveries.Where(d => d.Status == EmailDeliveryStatus.Pending
                && (d.ExpiresAtUtc <= now || d.Attempts >= MaxAttempts)
                && (d.LeasedUntilUtc == null || d.LeasedUntilUtc <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, EmailDeliveryStatus.Failed)
                .SetProperty(d => d.ProtectedPayload, (string?)null)
                .SetProperty(d => d.CompletedAtUtc, now)
                .SetProperty(d => d.LastError, "ExpiredOrAttemptsExhausted")
                .SetProperty(d => d.LeaseId, (Guid?)null)
                .SetProperty(d => d.LeasedUntilUtc, (DateTimeOffset?)null), cancellationToken);
        if (exhausted > 0) logger.LogError("Closed {Count} expired or exhausted email deliveries.", exhausted);
        var ids = await db.EmailDeliveries.AsNoTracking().Where(d => d.Status == EmailDeliveryStatus.Pending
                && d.NextAttemptUtc <= now && (d.LeasedUntilUtc == null || d.LeasedUntilUtc <= now))
            .OrderBy(d => d.NextAttemptUtc).Take(25).Select(d => d.Id).ToListAsync(cancellationToken);
        foreach (var id in ids) await DeliverAsync(id, cancellationToken);
        return ids.Count;
    }

    public async Task<int> PurgeCompletedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var before = clock.GetUtcNow().AddDays(-30);
        return await db.EmailDeliveries.Where(d => d.Status != EmailDeliveryStatus.Pending && d.CompletedAtUtc < before)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
