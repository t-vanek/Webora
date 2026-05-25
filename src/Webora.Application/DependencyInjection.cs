using Microsoft.Extensions.DependencyInjection;
using Webora.Application.Mapping;

namespace Webora.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers application-layer services. Wolverine message handlers in this assembly are
    /// discovered separately by the host via <see cref="IApplicationMarker"/>.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Mapperly mappers are stateless and generated at compile time — register as singletons.
        services.AddSingleton<AccountAuditMapper>();
        services.AddSingleton<NotificationMapper>();

        return services;
    }
}
