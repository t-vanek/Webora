using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Oversight;
using D3Parking.Application.Parking;

namespace D3Parking.Web.Parking;

/// <summary>
/// Periodically sends planning reminders and performs scheduled parking housekeeping. The interval
/// is read from the database each cycle, so
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
            var residentSpots = scope.ServiceProvider.GetRequiredService<IResidentSpotService>();
            var trust = scope.ServiceProvider.GetRequiredService<ITrustService>();
            var collusion = scope.ServiceProvider.GetRequiredService<ICollusionService>();
            var availability = scope.ServiceProvider.GetRequiredService<IAvailabilityCampaignService>();
            var oversight = scope.ServiceProvider.GetRequiredService<IOversightService>();

            // Every step is isolated: a transient failure in one (a database hiccup, a mail server
            // that is down) must not skip the ones behind it — the waitlist still needs
            // processing. Each failure is logged on its own and the cycle carries on.
            interval = await StepAsync("sweep interval", () => settings.GetSweepIntervalAsync(cancellationToken), FallbackInterval, cancellationToken);
            var reminded = await StepAsync("reservation reminders", () => reservations.SendDueRemindersAsync(cancellationToken), 0, cancellationToken);
            var planReleased = await StepAsync("usage-plan releases", () => residentSpots.ApplyDuePlanReleasesAsync(cancellationToken), 0, cancellationToken);
            var reconciled = await StepAsync("unused share reconciliation", () => residentSpots.ReconcileUnusedSharesAsync(cancellationToken), 0, cancellationToken);
            var credited = await StepAsync("monthly credit grants", () => reservations.GrantDueMonthlyCreditsAsync(cancellationToken), 0, cancellationToken);
            var queueOffers = await StepAsync("queue processing", () => reservations.ProcessQueueAsync(cancellationToken), 0, cancellationToken);
            var decayed = await StepAsync("reputation decay", () => reservations.DecayReputationAsync(cancellationToken), 0, cancellationToken);
            // Null until today's peak window has ended — adapting from a live (or empty) window
            // drained the surcharge to its minimum every night.
            var peakOccupancy = await StepAsync("peak occupancy", () => reservations.MeasurePeakOccupancyAsync(cancellationToken), (double?)null, cancellationToken);
            var repriced = peakOccupancy is { } measuredPeak
                && await StepAsync("adaptive pricing", () => settings.AdaptPeakSurchargeAsync(measuredPeak, cancellationToken), false, cancellationToken);
            var trustScored = await StepAsync("trust graph", () => trust.ComputeTrustAsync(cancellationToken), 0, cancellationToken);
            var collusionFlagged = await StepAsync("collusion scan", () => collusion.ScanAsync(cancellationToken), 0, cancellationToken);
            var campaignRecipients = await StepAsync("availability campaigns", () => availability.RunDueCampaignsAsync(cancellationToken), 0, cancellationToken);
            // Last, so the flags this very cycle raised are already on the oversight desk. The
            // queue's own load reconciles too, so this is the backstop for a lot nobody opens.
            var casesOpened = await StepAsync("oversight case ingest", () => oversight.EnsureCasesAsync(cancellationToken), 0, cancellationToken);
            // And then the desk's own turn: deadlines that have passed, signals that moved, the digest.
            var casesFollowedUp = await StepAsync("oversight follow-up", () => oversight.RunDueCaseWorkAsync(cancellationToken), 0, cancellationToken);

            if (reminded > 0 || planReleased > 0 || reconciled > 0 || credited > 0 || queueOffers > 0 || decayed > 0 || repriced || trustScored > 0 || collusionFlagged > 0 || campaignRecipients > 0 || casesOpened > 0 || casesFollowedUp > 0)
            {
                logger.LogInformation(
                    "Parking maintenance: {Reminded} reservation reminders, {PlanReleased} usage-plan releases, {Reconciled} unused shares reversed, {Credited} monthly credit grants, {QueueOffers} queue offers, {Decayed} reputation decays, adaptive reprice={Repriced}, {TrustScored} trust scores, {CollusionFlagged} collusion flags, {CampaignRecipients} availability-tip recipients, {CasesOpened} oversight cases opened, {CasesFollowedUp} oversight cases followed up.",
                    reminded, planReleased, reconciled, credited, queueOffers, decayed, repriced, trustScored, collusionFlagged, campaignRecipients, casesOpened, casesFollowedUp);
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

    /// <summary>
    /// Runs one maintenance step, containing its failure so the rest of the cycle still runs.
    /// Cancellation is rethrown — that is the host shutting down, not a step-level failure.
    /// </summary>
    private async Task<T> StepAsync<T>(string step, Func<Task<T>> action, T fallback, CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Parking maintenance step '{Step}' failed; continuing with the rest of the cycle.", step);
            return fallback;
        }
    }
}
