using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Webora.Application.Parking;
using Webora.Infrastructure.Parking;

namespace Webora.Web.Parking;

/// <summary>
/// Periodically sends reservation reminders and resolves no-shows, so the lot stays accurate
/// without anyone having to press a button.
/// </summary>
public sealed class ParkingMaintenanceService(
    IServiceScopeFactory scopeFactory,
    IOptions<ParkingOptions> options,
    ILogger<ParkingMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once at startup, then on the configured interval.
        await RunOnceAsync(stoppingToken);

        var interval = options.Value.SweepInterval;
        if (interval <= TimeSpan.Zero)
        {
            interval = TimeSpan.FromMinutes(5);
        }

        using var timer = new PeriodicTimer(interval);
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
