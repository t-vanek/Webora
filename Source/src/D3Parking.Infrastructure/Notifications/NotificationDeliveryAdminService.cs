using D3Parking.Application;
using D3Parking.Application.Notifications;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Notifications;
using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace D3Parking.Infrastructure.Notifications;

public sealed class NotificationDeliveryAdminService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    TimeProvider timeProvider) : INotificationDeliveryAdminService
{
    public async Task<NotificationDeliverySummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var sentSince = now.AddHours(-24);

        var counts = await dbContext.NotificationEmailDeliveries.AsNoTracking()
            .GroupBy(d => d.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

        var dueNow = await dbContext.NotificationEmailDeliveries.AsNoTracking()
            .CountAsync(d => d.Status == NotificationDeliveryStatus.Pending && d.NextAttemptUtc <= now,
                cancellationToken);
        var sentLast24Hours = await dbContext.NotificationEmailDeliveries.AsNoTracking()
            .CountAsync(d => d.Status == NotificationDeliveryStatus.Sent && d.SentAtUtc >= sentSince,
                cancellationToken);

        return new NotificationDeliverySummary(
            counts.GetValueOrDefault(NotificationDeliveryStatus.Pending),
            dueNow,
            counts.GetValueOrDefault(NotificationDeliveryStatus.Failed),
            sentLast24Hours);
    }

    public async Task<PagedResult<NotificationDeliveryListItem>> SearchAsync(
        string? search,
        NotificationDeliveryStatus? status,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, 100);
        var index = Math.Max(0, pageIndex);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = from delivery in dbContext.NotificationEmailDeliveries.AsNoTracking()
                    join user in dbContext.Users.AsNoTracking() on delivery.UserId equals user.Id into users
                    from user in users.DefaultIfEmpty()
                    select new { delivery, user };

        if (status is { } selectedStatus)
        {
            query = query.Where(row => row.delivery.Status == selectedStatus);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(row =>
                EF.Functions.Like(row.delivery.Title, term)
                || EF.Functions.Like(row.delivery.Message, term)
                || (row.user != null && row.user.Email != null && EF.Functions.Like(row.user.Email, term))
                || (row.user != null && row.user.DisplayName != null && EF.Functions.Like(row.user.DisplayName, term)));
        }

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResult<NotificationDeliveryListItem>.Empty(size);
        }

        index = Math.Min(index, (total - 1) / size);
        var rows = await query
            .OrderByDescending(row => row.delivery.CreatedAtUtc)
            .ThenByDescending(row => row.delivery.Id)
            .Skip(index * size)
            .Take(size)
            .Select(row => new NotificationDeliveryListItem(
                row.delivery.Id,
                row.delivery.UserId,
                row.user == null ? null : row.user.Email,
                row.user == null ? null : row.user.DisplayName,
                row.delivery.Title,
                row.delivery.Message,
                row.delivery.ActionText,
                row.delivery.ActionUrl,
                row.delivery.DeadlineText,
                row.delivery.Status,
                row.delivery.Attempts,
                row.delivery.ManualRetryCount,
                row.delivery.CreatedAtUtc,
                row.delivery.NextAttemptUtc,
                row.delivery.LastAttemptUtc,
                row.delivery.SentAtUtc,
                row.delivery.LastError))
            .ToListAsync(cancellationToken);

        return new PagedResult<NotificationDeliveryListItem>(rows, total, index, size);
    }

    public async Task<NotificationDeliveryRetryResult> RetryAsync(
        Guid deliveryId,
        Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var delivery = await dbContext.NotificationEmailDeliveries
            .FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);
        if (delivery is null)
        {
            return NotificationDeliveryRetryResult.NotFound;
        }

        var previousAttempts = delivery.Attempts;
        var now = timeProvider.GetUtcNow();
        if (!delivery.QueueManualRetry(now))
        {
            return NotificationDeliveryRetryResult.NotFailed;
        }

        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            delivery.UserId,
            AccountAuditEventType.NotificationDeliveryRetried,
            $"admin:{actingUserId}",
            $"delivery={delivery.Id}; previousAttempts={previousAttempts}; retry={delivery.ManualRetryCount}",
            now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return NotificationDeliveryRetryResult.Queued;
        }
        catch (DbUpdateConcurrencyException)
        {
            // A second administrator may have queued the same failed item from a stale page. The
            // rowversion makes that a harmless no-op instead of writing two audit events.
            return NotificationDeliveryRetryResult.NotFailed;
        }
    }
}
