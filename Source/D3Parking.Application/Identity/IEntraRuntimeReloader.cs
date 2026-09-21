namespace D3Parking.Application.Identity;

/// <summary>
/// Applies changed Entra ID settings to the running application.
/// </summary>
/// <remarks>
/// The settings service owns the values but knows nothing about the authentication stack that
/// consumes them; the web layer supplies the implementation that adds, removes or reconfigures the
/// OpenID Connect scheme. Without this hop, turning sign-in on would need a restart — which is a
/// poor answer to give someone who just used a settings page.
/// </remarks>
public interface IEntraRuntimeReloader
{
    /// <summary>Makes the running application reflect <paramref name="effective"/>.</summary>
    /// <remarks>
    /// The merged settings are passed in rather than read back, both to save a round trip and to
    /// break the dependency cycle that would otherwise form: the settings service calls the
    /// reloader, and a reloader that had to ask the settings service what to apply would need the
    /// service that constructs it.
    /// </remarks>
    Task ReloadAsync(EntraIdOptions effective, CancellationToken cancellationToken = default);
}

/// <summary>Used where nothing needs reloading — the background hosts and the test harness.</summary>
public sealed class NullEntraRuntimeReloader : IEntraRuntimeReloader
{
    public Task ReloadAsync(EntraIdOptions effective, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
