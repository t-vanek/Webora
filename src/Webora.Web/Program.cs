using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.SignalR;
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
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

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

// Real-time messaging + per-user notification delivery over SignalR.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddSingleton<INotificationRealtimePublisher, SignalRNotificationPublisher>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Honor the ingress-provided scheme/host so canonical-domain enforcement sees the public values
// rather than the internal proxy connection. Assumes a trusted reverse proxy in front of the app.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
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
