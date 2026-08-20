using D3Parking.Application.Notifications;

namespace D3Parking.Web.Notifications;

/// <summary>Drains the durable notification email outbox without delaying user actions.</summary>
public sealed class NotificationDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationDeliveryWorker> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(30);
    private DateTimeOffset _nextPurgeUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var hadWork = false;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<INotificationDeliveryDispatcher>();
                hadWork = await dispatcher.DeliverDueAsync(cancellationToken: stoppingToken) > 0;

                var now = timeProvider.GetUtcNow();
                if (now >= _nextPurgeUtc)
                {
                    var removed = await dispatcher.PurgeCompletedAsync(now.Subtract(CompletedRetention), stoppingToken);
                    if (removed > 0)
                    {
                        logger.LogInformation("Removed {Count} expired notification email delivery records.", removed);
                    }
                    _nextPurgeUtc = now.AddDays(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification email outbox dispatch failed.");
            }

            await Task.Delay(hadWork ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15),
                timeProvider, stoppingToken);
        }
    }
}
