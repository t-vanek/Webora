using D3Parking.Infrastructure.Email;

namespace D3Parking.Web.Email;

/// <summary>Drains account/general emails independently of notification preferences and delivery delays.</summary>
public sealed class EmailDeliveryWorker(
    IServiceScopeFactory scopes, TimeProvider clock, ILogger<EmailDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurge = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var count = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<EmailDeliveryDispatcher>();
                count = await dispatcher.DeliverDueAsync(stoppingToken);
                if (clock.GetUtcNow() >= nextPurge)
                {
                    await dispatcher.PurgeCompletedAsync(stoppingToken);
                    nextPurge = clock.GetUtcNow().AddDays(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Email outbox dispatch failed ({ErrorType}).", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(count > 0 ? 1 : 15), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
