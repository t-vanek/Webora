using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.FluentUI.AspNetCore.Components;
using Serilog;
using Webora.Application;
using Webora.Application.Notifications;
using Webora.Infrastructure;
using Webora.Web;
using Webora.Web.Components;
using Webora.Web.Hosting;
using Webora.Web.Hubs;
using Webora.Web.Identity;
using Webora.Web.Notifications;
using Webora.Web.Parking;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

// Enable legacy single-byte charsets (e.g. windows-1250, iso-8859-2) for page/email encoding.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// Application + infrastructure layers (EF Core/Postgres, Redis, OpenIddict stores, identity seeder).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ASP.NET Core Identity (cookie sign-in) + OpenIddict authorization server + permission-based RBAC.
builder.Services.AddWeboraIdentity();
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

// Wolverine messaging: discovers handlers in the application assembly, integrates with EF Core
// transactions, and (when configured) publishes/consumes over RabbitMQ.
builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(IApplicationMarker).Assembly);

    // Hook handler transactions into EF Core's SaveChanges (transactional outbox/inbox).
    opts.UseEntityFrameworkCoreTransactions();

    var rabbitConnection = builder.Configuration.GetConnectionString("RabbitMq");
    if (!string.IsNullOrWhiteSpace(rabbitConnection))
    {
        opts.UseRabbitMq(new Uri(rabbitConnection)).AutoProvision();
    }
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
    .AddAdditionalAssemblies(typeof(Webora.Web.Client._Imports).Assembly);

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
