using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Application.Notifications;
using D3Parking.Domain.Notifications;
using D3Parking.Infrastructure.Email;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Notifications;

public sealed class NotificationDeliveryDispatcher(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IEmailTransport emailTransport,
    IStringLocalizer<ParkingMessages> messages,
    ILogger<NotificationDeliveryDispatcher> logger,
    TimeProvider timeProvider) : INotificationDeliveryDispatcher
{
    private static readonly TimeSpan DeliveryLease = TimeSpan.FromMinutes(5);

    public async Task DeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var claimTime = timeProvider.GetUtcNow();
        // Moving NextAttemptUtc forward is an atomic, recoverable lease. It prevents two web
        // instances from sending the same due row; after a process crash another instance can
        // reclaim it once the lease expires.
        var claimed = await dbContext.NotificationEmailDeliveries
            .Where(d => d.Id == deliveryId
                && d.Status == NotificationDeliveryStatus.Pending
                && d.NextAttemptUtc <= claimTime)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.NextAttemptUtc, claimTime.Add(DeliveryLease)), cancellationToken);
        if (claimed == 0) return;

        var delivery = await dbContext.NotificationEmailDeliveries
            .FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
        if (delivery is null) return;

        var recipient = await dbContext.Users.AsNoTracking().Where(u => u.Id == delivery.UserId)
            .Select(u => new { u.Email, u.DisplayName }).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(recipient?.Email))
        {
            delivery.MarkFailed(timeProvider.GetUtcNow(), "Recipient has no email address.");
            if (delivery.Status == NotificationDeliveryStatus.Failed)
            {
                logger.LogError("Notification email delivery {DeliveryId} permanently failed: user {UserId} has no email address.",
                    delivery.Id, delivery.UserId);
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var reason = messages["Email_Chrome_Reason"].Value;
            // This outbox already owns persistence, retries and delivery state. Send through the
            // transport directly so "Sent" means the SMTP server actually accepted the message,
            // rather than merely accepting it into the volatile in-process queue.
            await emailTransport.SendAsync(new EmailMessage
            {
                To = recipient.Email,
                ToName = recipient.DisplayName,
                Subject = delivery.Title,
                HtmlBody = BrandedEmail.RenderHtml(new BrandedEmail.Content(
                    delivery.Title, System.Net.WebUtility.HtmlEncode(delivery.Message), reason,
                    messages["Email_Chrome_Settings"].Value, delivery.ActionText, delivery.ActionUrl, delivery.DeadlineText)),
                TextBody = BrandedEmail.RenderText(delivery.Title, delivery.Message,
                    delivery.ActionUrl, delivery.DeadlineText, reason),
            }, cancellationToken);
            delivery.MarkSent(timeProvider.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            delivery.MarkFailed(timeProvider.GetUtcNow(), ex.GetBaseException().Message);
            if (delivery.Status == NotificationDeliveryStatus.Failed)
            {
                logger.LogError(ex, "Notification email delivery {DeliveryId} permanently failed after {Attempt} attempts.",
                    delivery.Id, delivery.Attempts);
            }
            else
            {
                logger.LogWarning(ex, "Notification email delivery {DeliveryId} failed on attempt {Attempt}.",
                    delivery.Id, delivery.Attempts);
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DeliverDueAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var ids = await dbContext.NotificationEmailDeliveries.AsNoTracking()
            .Where(d => d.Status == NotificationDeliveryStatus.Pending && d.NextAttemptUtc <= now)
            .OrderBy(d => d.NextAttemptUtc).Take(Math.Clamp(take, 1, 500))
            .Select(d => d.Id).ToListAsync(cancellationToken);
        foreach (var id in ids) await DeliverAsync(id, cancellationToken);
        return ids.Count;
    }

    public async Task<int> PurgeCompletedAsync(DateTimeOffset createdBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.NotificationEmailDeliveries
            .Where(d => (d.Status == NotificationDeliveryStatus.Sent
                    || d.Status == NotificationDeliveryStatus.Failed)
                && d.CreatedAtUtc < createdBeforeUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
