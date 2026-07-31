using D3Parking.Application.Identity;

namespace D3Parking.Web.Identity;

/// <summary>
/// Keeps this instance's scheme map in step with the stored Entra ID settings.
/// </summary>
/// <remarks>
/// A save reconciles only the instance that handled it: both the settings snapshot and the scheme
/// map live in process memory. Behind more than one instance that is a split brain — the sign-in
/// button appears everywhere, because every instance re-reads the settings when its snapshot
/// expires, but only the one that took the save has a handler to answer the challenge. Everywhere
/// else the challenge finds no scheme and lands on the generic external-sign-in error, for as long
/// as the process lives. Switching sign-in off is the same in reverse: an instance that never heard
/// about it keeps accepting federated sign-ins.
///
/// Polling rather than a distributed cache or a message: the state to converge on is one row and
/// one boolean, the reconciliation is idempotent and free when nothing changed
/// (<see cref="EntraSchemeReloader"/>), and this way the deployment needs no infrastructure it does
/// not already have.
/// </remarks>
public sealed class EntraSchemeSynchronizer(
    IEntraSettingsService settings,
    IEntraRuntimeReloader reloader,
    ILogger<EntraSchemeSynchronizer> logger) : BackgroundService
{
    /// <summary>
    /// Matched to the settings snapshot's own lifetime, so a poll costs a database read at most
    /// every other cycle. A change is live everywhere inside a minute.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // The first reconciliation already happened during startup (UseEntraIdAuthenticationAsync),
        // so this begins by waiting rather than by repeating it.
        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await reloader.ReloadAsync(await settings.GetEffectiveAsync(stoppingToken), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A database that is briefly unreachable must not take the host down or stop the
                // polling; the next cycle picks the settings up.
                logger.LogWarning(ex, "Could not reconcile the Entra ID sign-in scheme; retrying in {Interval}", Interval);
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
