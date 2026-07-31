using D3Parking.Application.Oversight;
using D3Parking.Application.Parking;
using D3Parking.Domain.Authorization;
using D3Parking.Web.Authorization;

namespace D3Parking.Web.Parking;

/// <summary>
/// Streams the pictures attached to oversight cases. Each is guarded by the permission that may
/// see its kind of case, never by a shared one: a mismatch photo can show a stranger's car and
/// plate and belongs to the reviewers alone, while a picture of a pothole is for whoever maintains
/// the lot. Neither is ever a public asset.
/// </summary>
public static class MismatchPhotoEndpoints
{
    public static IEndpointRouteBuilder MapMismatchPhotoApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/parking/mismatches/{id:guid}/photo",
            async (Guid id, IParkingSpotService spots, CancellationToken ct) =>
            {
                var photo = await spots.GetMismatchPhotoAsync(id, ct);
                return photo is null
                    ? Results.NotFound()
                    : Results.File(photo.Content, photo.ContentType);
            }).RequireAuthorization(PermissionPolicies.For(Permissions.Parking.ReviewMismatches));

        app.MapGet("/api/parking/defects/{id:guid}/photo",
            async (Guid id, IOversightService oversight, CancellationToken ct) =>
            {
                var photo = await oversight.GetDefectPhotoAsync(id, ct);
                return photo is null
                    ? Results.NotFound()
                    : Results.File(photo.Content, photo.ContentType);
            }).RequireAuthorization(PermissionPolicies.For(Permissions.Parking.ManageSpots));

        return app;
    }
}
