using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Webora.Application.Accounts;
using Webora.Application.Administration;
using Webora.Application.Notifications;
using Webora.Application.Parking;
using Webora.Application.Settings;
using Webora.Infrastructure.Accounts;
using Webora.Infrastructure.Administration;
using Webora.Infrastructure.Parking;
using Webora.Infrastructure.Settings;
using Webora.Infrastructure.Email;
using Webora.Infrastructure.Identity;
using Webora.Infrastructure.Notifications;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddPersistence(services, configuration);
        AddCaching(services, configuration);
        AddOpenIddictCore(services);
        services.AddEmail(configuration);

        services.AddSingleton(TimeProvider.System);
        services.Configure<AccountOptions>(configuration.GetSection(AccountOptions.SectionName));
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
        services.AddScoped<IRoleAdminService, RoleAdminService>();
        services.AddScoped<ISiteSettingsService, SiteSettingsService>();

        // In-app notifications. The web host replaces the publisher with a SignalR implementation.
        services.AddScoped<INotificationService, NotificationService>();
        services.TryAddSingleton<INotificationRealtimePublisher, NullNotificationRealtimePublisher>();

        // Parking reservations and the incentive system. The tunable policy is stored in the
        // database (admin-editable) and read through IParkingSettingsService (cached).
        services.AddScoped<IParkingSettingsService, ParkingSettingsService>();
        services.AddScoped<IParkingSpotService, ParkingSpotService>();
        services.AddScoped<IReservationService, ReservationService>();
        services.AddScoped<IIncentiveService, IncentiveService>();
        services.AddScoped<IResidentSpotService, ResidentSpotService>();
        services.AddScoped<IUserLocationService, UserLocationService>();

        // Distance scaling for the shared-spot reward. Haversine works offline; a driving-distance
        // provider can replace IDistanceProvider, and the geocoder base URL is configurable.
        services.Configure<GeocodingOptions>(configuration.GetSection(GeocodingOptions.SectionName));
        services.AddSingleton<IDistanceProvider, HaversineDistanceProvider>();
        services.AddHttpClient<IGeocoder, NominatimGeocoder>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<GeocodingOptions>>().Value;
            var baseUrl = options.NominatimBaseUrl.EndsWith('/') ? options.NominatimBaseUrl : options.NominatimBaseUrl + "/";
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // ASP.NET Core Identity itself (sign-in, cookies, token providers) is wired in the web
        // host where the ASP.NET shared framework is available. Here we only provide the seeder
        // and its options.
        services.Configure<IdentitySeedOptions>(configuration.GetSection(IdentitySeedOptions.SectionName));
        services.AddScoped<IdentitySeeder>();

        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is not configured.");

        // IDbContextFactory lets services create a fresh DbContext per operation, which is required
        // for Blazor Server where concurrent component initialization can race on a shared scoped
        // context. The scoped WeboraDbContext registration is kept (delegated to the factory) so
        // ASP.NET Identity, OpenIddict, and other framework consumers continue to receive one
        // tracked context per request as they expect.
        services.AddDbContextFactory<WeboraDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<WeboraDbContext>>().CreateDbContext());
    }

    private static void AddCaching(IServiceCollection services, IConfiguration configuration)
    {
        var redisConnection = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redisConnection))
        {
            return;
        }

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnection;
            options.InstanceName = "webora:";
        });
    }

    private static void AddOpenIddictCore(IServiceCollection services)
    {
        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                    .UseDbContext<WeboraDbContext>();
            });
    }
}
