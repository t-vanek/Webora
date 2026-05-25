using Microsoft.Extensions.DependencyInjection;

namespace Webora.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers application-layer services. Wolverine message handlers in this assembly are
    /// discovered separately by the host via <see cref="IApplicationMarker"/>.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        return services;
    }
}
