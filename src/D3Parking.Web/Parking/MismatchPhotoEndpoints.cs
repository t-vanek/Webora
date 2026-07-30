using D3Parking.Application.Parking;
using D3Parking.Domain.Authorization;
using D3Parking.Web.Authorization;

namespace D3Parking.Web.Parking;

/// <summary>
/// Streams the photo proof of an occupancy mismatch to the spot manager's review. Guarded by the
/// same permission as the mismatch page itself — the picture may show a stranger's car and plate,
/// so it is for the reviewers' eyes only, never a public asset.
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
            }).RequireAuthorization(PermissionPolicies.For(Permissions.Parking.ManageSpots));

        return app;
    }
}
