using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Webora.Application.Parking;

namespace Webora.Web.Parking;

/// <summary>
/// Periodically sends reservation reminders and resolves no-shows, so the lot stays accurate
/// without anyone having to press a button.
/// </summary>
public sealed class ParkingMaintenanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<ParkingMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once at startup, then on the interval.
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var reservations = scope.ServiceProvider.GetRequiredService<IReservationService>();

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
    }
}
