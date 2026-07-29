using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.FluentUI.AspNetCore.Components;
using Serilog;
using D3Parking.Application;
using D3Parking.Application.Notifications;
using D3Parking.Application.Settings;
using D3Parking.Infrastructure;
using D3Parking.Web;
using D3Parking.Web.Components;
using D3Parking.Web.Hosting;
using D3Parking.Web.Hubs;
using D3Parking.Web.Identity;
using D3Parking.Web.Notifications;
using D3Parking.Web.Parking;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;

// Enable legacy single-byte charsets (e.g. windows-1250, iso-8859-2) for page/email encoding.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// Application + infrastructure layers (EF Core/SQL Server, OpenIddict stores, identity seeder).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ASP.NET Core Identity (cookie sign-in) + OpenIddict authorization server + permission-based RBAC.
builder.Services.AddD3ParkingIdentity();
builder.Services.AddIdentityServer();
builder.Services.AddPermissionAuthorization();

// Fluent UI Blazor components (providers go into MainLayout). Available in both server-rendered
// and WebAssembly components — the WASM client also registers AddFluentUIComponents() in its host.
builder.Services.AddFluentUIComponents();

// Real-time messaging + per-user notification delivery over SignalR.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddSingleton<INotificationRealtimePublisher, SignalRNotificationPublisher>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Background maintenance: sends reservation reminders and resolves no-shows on a schedule.
builder.Services.AddHostedService<ParkingMaintenanceService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Honor the ingress-provided scheme/host so canonical-domain enforcement sees the public values
// rather than the internal proxy connection. By default only loopback is trusted; configure the
// reverse proxy under "ForwardedHeaders" (KnownProxies/KnownNetworks, or TrustAllProxies).
var forwardedHeaders = builder.Configuration.GetSection("ForwardedHeaders");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

    if (forwardedHeaders.GetValue<bool>("TrustAllProxies"))
    {
        // Accept forwarded headers from any proxy. Only safe behind a fully controlled ingress.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
    else
    {
        // Keep the framework default (loopback only) and add explicitly trusted proxies/networks.
        foreach (var proxy in forwardedHeaders.GetSection("KnownProxies").Get<string[]>() ?? [])
        {
            if (IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        }

        foreach (var network in forwardedHeaders.GetSection("KnownNetworks").Get<string[]>() ?? [])
        {
            if (System.Net.IPNetwork.TryParse(network, out var ipNetwork))
            {
                options.KnownIPNetworks.Add(ipNetwork);
            }
        }
    }
});

// Localization. Czech is the default; cultures are negotiated from the culture cookie (set by the
// language switcher) and then the browser's Accept-Language header.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture(SupportedCultures.Default);
    options.AddSupportedCultures(SupportedCultures.All);
    options.AddSupportedUICultures(SupportedCultures.All);

    // Fall back to the site's configured default language (after cookie/Accept-Language) before
    // the static default culture.
    options.RequestCultureProviders.Add(new SiteDefaultCultureProvider());
});

// Flow the authenticated user into Blazor components (and persist it to the WebAssembly client).
builder.Services.AddCascadingAuthenticationState();

// Blazor Web App with both interactive render modes.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization(options => options.SerializeAllClaims = true);

// Wolverine messaging: discovers handlers in the application assembly and runs them on in-process
// local queues — no external broker. Email is the main user of this (see QueuedEmailSender).
builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(IApplicationMarker).Assembly);

    // Hook handler transactions into EF Core's SaveChanges (transactional outbox/inbox).
    opts.UseEntityFrameworkCoreTransactions();

    // Background work retries on its own instead of failing the request that queued it. A mail
    // server that is down for longer than this drops the message — it can be requested again.
    opts.OnAnyException()
        .RetryWithCooldown(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseForwardedHeaders();

// Applies the configured page charset (re-encodes text/html when it is not UTF-8).
app.UsePageCharset();

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Canonical host/scheme redirects and HSTS, driven by the stored site settings. Skipped in
// Development so local http://localhost runs untouched.
if (!app.Environment.IsDevelopment())
{
    app.UseDomainEnforcement();
}

app.UseRequestLocalization();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(D3Parking.Web.Client._Imports).Assembly);

app.MapHub<NotificationsHub>(NotificationsHub.Path);
app.MapNotificationApi();

// Antiforgery token endpoint for the WASM client. The client fetches the token once and attaches
// it as the RequestVerificationToken header on every POST/PUT/DELETE, so cookie-auth POSTs are
// protected against CSRF in addition to the default SameSite=Lax cookie defense.
app.MapGet("/api/antiforgery/token", (HttpContext context, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { token = tokens.RequestToken });
}).RequireAuthorization();

// Web app manifest (PWA). Generated dynamically so the installed app inherits the configured
// site identity and the negotiated request culture, same as the document head in App.razor.
app.MapGet("/manifest.webmanifest", async (ISiteSettingsService siteSettings, CancellationToken cancellationToken) =>
{
    var identity = await siteSettings.GetIdentityAsync(cancellationToken);
    var name = string.IsNullOrWhiteSpace(identity.Name) ? "D3Parking" : identity.Name.Trim();

    return Results.Json(new
    {
        name,
        short_name = name,
        description = identity.Description ?? string.Empty,
        lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
        id = "/",
        start_url = "/",
        scope = "/",
        display = "standalone",
        background_color = "#023444",
        theme_color = "#023444",
        icons = new object[]
        {
            new { src = "icons/icon-192.png", sizes = "192x192", type = "image/png" },
            new { src = "icons/icon-512.png", sizes = "512x512", type = "image/png" },
            new { src = "icons/icon-maskable-192.png", sizes = "192x192", type = "image/png", purpose = "maskable" },
            new { src = "icons/icon-maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" },
        },
    }, contentType: "application/manifest+json");
});

// Language switcher: persists the chosen culture in a cookie and returns to the page.
app.MapGet("/culture/set", (string culture, string? redirectUri, HttpContext context) =>
{
    if (SupportedCultures.All.Contains(culture))
    {
        context.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, Path = "/" });
    }

    return Results.LocalRedirect(string.IsNullOrEmpty(redirectUri) ? "/" : redirectUri);
});

// Apply migrations (development) and seed roles, permissions and the admin account.
await app.SeedIdentityAsync();

app.Run();
