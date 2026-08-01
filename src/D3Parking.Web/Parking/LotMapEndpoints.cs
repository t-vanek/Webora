using D3Parking.Application.Parking.Maps;
using D3Parking.Domain.Authorization;
using D3Parking.Web.Authorization;

namespace D3Parking.Web.Parking;

/// <summary>
/// Streams a map's traced-over site plan. Guarded by <see cref="Permissions.Parking.View"/> rather
/// than by the editor's own permission: the scan is the building's public site plan, it carries no
/// personal data, and every parker needs it the moment a map is published behind the booking screen.
/// It is still never an anonymous asset.
/// </summary>
public static class LotMapEndpoints
{
    public static IEndpointRouteBuilder MapLotMapApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/parking/maps/{id:guid}/background",
            async (Guid id, HttpContext http, ILotMapService maps, CancellationToken ct) =>
            {
                var background = await maps.GetBackgroundAsync(id, ct);
                if (background is null)
                {
                    return Results.NotFound();
                }

                // The stored type was detected from the bytes on upload, so it is already one of
                // three raster formats. nosniff is the belt to that braces: it stops a browser from
                // deciding for itself that an image is really a document worth executing.
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(background.Content, background.ContentType);
            }).RequireAuthorization(PermissionPolicies.For(Permissions.Parking.View));

        return app;
    }
}
