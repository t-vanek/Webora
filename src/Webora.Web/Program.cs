using Serilog;
using Webora.Application;
using Webora.Infrastructure;
using Webora.Web;
using Webora.Web.Client.Pages;
using Webora.Web.Components;
using Webora.Web.Hubs;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// Application + infrastructure layers (EF Core/Postgres, Redis, Identity, OpenIddict stores).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// OpenIddict authorization server + validation.
builder.Services.AddIdentityServer();

// Real-time messaging.
builder.Services.AddSignalR();

// Blazor Web App with both interactive render modes.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

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
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Webora.Web.Client._Imports).Assembly);

app.MapHub<NotificationsHub>(NotificationsHub.Path);

app.Run();
