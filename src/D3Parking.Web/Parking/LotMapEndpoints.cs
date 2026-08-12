using D3Parking.Application.Parking;
using D3Parking.Domain.Authorization;
using D3Parking.Web.Authorization;

namespace D3Parking.Web.Parking;

public static class LotMapEndpoints
{
    public static IEndpointRouteBuilder MapLotMapApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/parking/orientation-map",
            async (HttpContext http, IParkingSettingsService settings, CancellationToken ct) =>
            {
                var map = await settings.GetOrientationMapAsync(ct);
                if (map is null)
                {
                    return Results.NotFound();
                }

                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(map.Content, map.ContentType);
            }).RequireAuthorization(PermissionPolicies.For(Permissions.Parking.View));

        return app;
    }
}
