using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using D3Parking.Application.Accounts;
using D3Parking.Application.Administration;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Infrastructure.Accounts;
using D3Parking.Infrastructure.Administration;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Settings;
using D3Parking.Infrastructure.Email;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Notifications;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddPersistence(services, configuration);
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

        // Web Push (VAPID). The publisher is only registered when keys are configured — the web
        // host folds it into its composite realtime publisher via GetService, and the API returns
        // 204 for the public key so the client hides the toggle.
        services.Configure<WebPushOptions>(configuration.GetSection(WebPushOptions.SectionName));
        var webPushOptions = configuration.GetSection(WebPushOptions.SectionName).Get<WebPushOptions>() ?? new WebPushOptions();
        if (webPushOptions.IsConfigured)
        {
            services.AddHttpClient<PushServiceClient>()
                .AddTypedClient(http => new PushServiceClient(http)
                {
                    DefaultAuthentication = new VapidAuthentication(webPushOptions.PublicKey, webPushOptions.PrivateKey)
                    {
                        Subject = webPushOptions.Subject,
                    },
                });
            services.AddScoped<WebPushNotificationPublisher>();
        }

        // Parking reservations and the incentive system. The tunable policy is stored in the
        // database (admin-editable) and read through IParkingSettingsService (cached).
        services.AddScoped<IParkingSettingsService, ParkingSettingsService>();
        services.AddScoped<IParkingSpotService, ParkingSpotService>();
        services.AddScoped<IReservationService, ReservationService>();
        services.AddScoped<IIncentiveService, IncentiveService>();
        services.AddScoped<IResidentSpotService, ResidentSpotService>();
        services.AddScoped<IUserLocationService, UserLocationService>();
        services.AddScoped<ITrustService, TrustService>();
        services.AddScoped<ICollusionService, CollusionService>();

        // Distance scaling for the shared-spot reward. Haversine works offline; switch to the OSRM
        // driving-distance provider via config ("Distance:Provider": "Osrm") — it falls back to
        // haversine if the routing service is unreachable. The geocoder base URL is configurable too.
        services.Configure<GeocodingOptions>(configuration.GetSection(GeocodingOptions.SectionName));
        services.Configure<DistanceOptions>(configuration.GetSection(DistanceOptions.SectionName));
        services.AddSingleton<HaversineDistanceProvider>();

        var distanceOptions = configuration.GetSection(DistanceOptions.SectionName).Get<DistanceOptions>() ?? new DistanceOptions();
        if (distanceOptions.UseOsrm)
        {
            var osrmBase = distanceOptions.OsrmBaseUrl.EndsWith('/') ? distanceOptions.OsrmBaseUrl : distanceOptions.OsrmBaseUrl + "/";
            services.AddHttpClient<OsrmDistanceProvider>(client =>
            {
                client.BaseAddress = new Uri(osrmBase);
                client.Timeout = TimeSpan.FromSeconds(15);
            });
            services.AddScoped<IDistanceProvider>(sp => sp.GetRequiredService<OsrmDistanceProvider>());
        }
        else
        {
            services.AddScoped<IDistanceProvider>(sp => sp.GetRequiredService<HaversineDistanceProvider>());
        }

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
        var connectionString = configuration.GetConnectionString("SqlServer")
            ?? throw new InvalidOperationException("Connection string 'SqlServer' is not configured.");

        // IDbContextFactory lets services create a fresh DbContext per operation, which is required
        // for Blazor Server where concurrent component initialization can race on a shared scoped
        // context. The scoped D3ParkingDbContext registration is kept (delegated to the factory) so
        // ASP.NET Identity, OpenIddict, and other framework consumers continue to receive one
        // tracked context per request as they expect.
        services.AddDbContextFactory<D3ParkingDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<D3ParkingDbContext>>().CreateDbContext());
    }

    private static void AddOpenIddictCore(IServiceCollection services)
    {
        services.AddOpenIddict()
            .AddCore(options =>
            {
                options.UseEntityFrameworkCore()
                    .UseDbContext<D3ParkingDbContext>();
            });
    }
}
