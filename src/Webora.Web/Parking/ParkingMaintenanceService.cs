using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Webora.Application.Parking;

namespace Webora.Web.Parking;

/// <summary>
/// Periodically sends reservation reminders and resolves no-shows, so the lot stays accurate
/// without anyone having to press a button. The interval is read from the database each cycle, so
/// changes to it take effect without a restart.
/// </summary>
public sealed class ParkingMaintenanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<ParkingMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = await RunOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<TimeSpan> RunOnceAsync(CancellationToken cancellationToken)
    {
        var interval = FallbackInterval;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<IParkingSettingsService>();
            var reservations = scope.ServiceProvider.GetRequiredService<IReservationService>();

            interval = await settings.GetSweepIntervalAsync(cancellationToken);
            var reminded = await reservations.SendDueRemindersAsync(cancellationToken);
            var resolved = await reservations.SweepNoShowsAsync(cancellationToken);

            if (reminded > 0 || resolved > 0)
            {
                logger.LogInformation(
                    "Parking maintenance: {Reminded} reminders sent, {Resolved} no-shows resolved.",
                    reminded, resolved);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Parking maintenance run failed.");
        }

        return interval <= TimeSpan.Zero ? FallbackInterval : interval;
    }
}
