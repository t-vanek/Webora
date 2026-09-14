using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace D3Parking.Web.Hosting;

public static class OperationalEndpoints
{
    // Branch before canonical redirects/localization: liveness must not need database settings.
    public static void UseOperationalEndpoints(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            var operational = path is "/health/live" or "/health/ready" or "/version";
            var localHealth = app.Configuration["Kestrel:Endpoints:Health:Url"];
            if (localHealth is not null && context.Connection.LocalPort == new Uri(localHealth).Port && !operational)
            {
                context.Response.StatusCode = 404;
                return;
            }
            if (!operational) { await next(context); return; }
            context.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsGet(context.Request.Method)) { context.Response.StatusCode = 405; return; }
            var release = ReleaseInformation.Read(app.Environment.EnvironmentName);
            if (path == "/version") { await context.Response.WriteAsJsonAsync(release); return; }
            if (path == "/health/live") { await context.Response.WriteAsJsonAsync(new { status = "live", release }); return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var factory = context.RequestServices.GetRequiredService<IDbContextFactory<D3ParkingDbContext>>();
                await using var db = await factory.CreateDbContextAsync(timeout.Token);
                await DeploymentDatabase.RequireCurrentSchemaAsync(db, timeout.Token);
                await context.Response.WriteAsJsonAsync(new { status = "ready", release });
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning("Readiness failed: {ErrorType}", ex.GetType().Name);
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { status = "not-ready", release });
            }
        });
    }
}
