using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Maps;

namespace D3Parking.Application.Parking.Maps;

/// <summary>
/// Authoring and storage of lot maps: the drawing engine behind the editor. Everything here is about
/// the geometry of the site — what the drawn shapes then <em>mean</em> on a given day (free, booked,
/// held) stays with <see cref="ILotDashboardService"/>, which the map only ever joins to by spot id.
/// </summary>
public interface ILotMapService
{
    Task<IReadOnlyList<LotMapSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The map and every shape on it. Null when the map is gone.</summary>
    Task<LotMapDetailDto?> GetAsync(Guid mapId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The map the driver-facing screens should draw: the single published one. Null while none is
    /// published, which is the deliberate "not ready yet" state rather than an error.
    /// </summary>
    Task<LotMapDetailDto?> GetPublishedAsync(CancellationToken cancellationToken = default);

    Task<ParkingResult> CreateAsync(string name, int width, int height, CancellationToken cancellationToken = default);

    Task<ParkingResult> UpdateAsync(
        Guid mapId,
        string name,
        int width,
        int height,
        int gridSize,
        int backgroundOpacity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes or withdraws the map. Publishing one withdraws whatever else was published: the
    /// driver-facing screens ask for "the" map, and two answers would make which one they get depend
    /// on row order.
    /// </summary>
    Task<ParkingResult> SetPublishedAsync(Guid mapId, bool published, CancellationToken cancellationToken = default);

    Task<ParkingResult> DeleteAsync(Guid mapId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the site-plan scan traced over in the editor. The content type is not a parameter on
    /// purpose — it is detected from the bytes (see <see cref="ImageContentType"/>), because what is
    /// stored is what the endpoint serves back from this origin.
    /// </summary>
    Task<ParkingResult> SetBackgroundAsync(Guid mapId, byte[] content, CancellationToken cancellationToken = default);

    Task<ParkingResult> ClearBackgroundAsync(Guid mapId, CancellationToken cancellationToken = default);

    /// <summary>The stored scan, for the endpoint that streams it. Null when the map has none.</summary>
    Task<MapBackgroundDto?> GetBackgroundAsync(Guid mapId, CancellationToken cancellationToken = default);

    /// <summary>Adds one shape and hands back the row that was written, ids and all.</summary>
    Task<MapShapeResult> AddShapeAsync(
        Guid mapId,
        MapShapeKind kind,
        MapRect rect,
        string? label,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Repeats a shape into a row, creating everything past the source. The created shapes come back
    /// so the editor can select them without re-reading the map.
    /// </summary>
    Task<MapShapeResult> AddRowAsync(Guid mapId, MapRowRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resizes the map's coordinate space to the proportions of the uploaded underlay, scaling every
    /// shape with it so a drawing already begun keeps its place on the plan.
    /// </summary>
    /// <remarks>
    /// Without this the underlay is stretched to whatever two numbers were typed when the map was
    /// created, and nothing on screen says what they should have been — so the plan is traced
    /// distorted. The natural pixel size of the image is the answer, and it is the browser that
    /// knows it.
    /// </remarks>
    Task<ParkingResult> MatchToBackgroundAsync(
        Guid mapId,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the given shapes, offset clear of the originals, and hands the copies back selected.
    /// This is what makes the second row of a double bay cheap: select the first, duplicate, drag.
    /// Labels are carried over as they are — renumbering is its own step, and guessing here would
    /// silently rename stalls.
    /// </summary>
    Task<MapShapeResult> DuplicateShapesAsync(
        Guid mapId,
        IReadOnlyList<Guid> shapeIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Relabels a selection in reading order, counting from <paramref name="firstLabel"/>. The
    /// rescue for a row traced perfectly and numbered one out — otherwise thirteen labels retyped
    /// by hand.
    /// </summary>
    Task<MapRenumberResult> RenumberShapesAsync(
        Guid mapId,
        IReadOnlyList<Guid> shapeIds,
        string firstLabel,
        int step,
        bool reverse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a set of labels as given. Exists so a renumbering can be undone: the previous labels
    /// are put back exactly, without a sequence being re-derived from them.
    /// </summary>
    Task<ParkingResult> SetLabelsAsync(
        Guid mapId,
        IReadOnlyList<MapShapeLabel> labels,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes what a whole selection represents. Reports how many links it broke: only a stall shape
    /// may stand for a spot, so turning ten of them into lanes quietly unlinks ten spots, and quietly
    /// is exactly what that must not be.
    /// </summary>
    Task<MapKindChangeResult> SetKindAsync(
        Guid mapId,
        IReadOnlyList<Guid> shapeIds,
        MapShapeKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>Renames a shape and/or changes what it represents.</summary>
    Task<ParkingResult> UpdateShapeAsync(
        Guid shapeId,
        string? label,
        MapShapeKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the geometry of everything a drag touched, in one transaction, and hands back what was
    /// actually stored — the rectangles may have been clamped back onto the map, and the canvas has to
    /// show that rather than the browser's proposal. Ids not on the map are ignored rather than
    /// failing the batch: a stale editor must not be able to move another map's shapes, and a shape
    /// deleted in another tab should not sink the drag that is landing.
    /// </summary>
    Task<MapMoveResult> MoveShapesAsync(
        Guid mapId,
        IReadOnlyList<ShapeGeometryUpdate> updates,
        CancellationToken cancellationToken = default);

    Task<ParkingResult> DeleteShapesAsync(Guid mapId, IReadOnlyList<Guid> shapeIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts deleted shapes back — what undo is built on. The restored shapes get fresh ids (the old
    /// rows are gone), and a spot link is re-established only where the spot is still free, so
    /// undoing a delete can never steal a rectangle somebody drew in the meantime.
    /// </summary>
    Task<MapShapeResult> RestoreShapesAsync(
        Guid mapId,
        IReadOnlyList<MapShapeRestore> shapes,
        CancellationToken cancellationToken = default);

    /// <summary>Binds a shape to a spot, or clears the binding when spotId is null.</summary>
    Task<ParkingResult> LinkSpotAsync(Guid shapeId, Guid? spotId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds every still-unlinked stall shape whose label matches a spot code, and reports what did
    /// not match either way. This is what turns a traced plan into a live board in one click.
    /// </summary>
    Task<MapAutoLinkResult> AutoLinkAsync(Guid mapId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates real, bookable spots named after the given shapes' labels and links each shape to its
    /// spot — the bridge from a drawing to a lot. A label that already names a spot links to that one
    /// instead of failing, so re-running over a part-built lot is safe.
    /// </summary>
    Task<MapSpotCreationResult> CreateSpotsFromShapesAsync(
        Guid mapId,
        IReadOnlyList<Guid> shapeIds,
        ParkingSpotType type,
        CancellationToken cancellationToken = default);

    /// <summary>The map as portable JSON (geometry and labels; not the background, not the spot links).</summary>
    Task<string?> ExportAsync(Guid mapId, CancellationToken cancellationToken = default);

    /// <summary>Creates a map from exported JSON. Always a new map — import never overwrites one.</summary>
    Task<ParkingResult> ImportAsync(string name, string json, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a renumbering: the labels as applied, in the order they were applied, so the editor can
/// say what happened without re-reading the map.
/// </summary>
public sealed record MapRenumberResult(bool Succeeded, IReadOnlyList<string> Labels, IReadOnlyList<string> Errors)
{
    public static MapRenumberResult Success(IReadOnlyList<string> labels) => new(true, labels, []);

    public static MapRenumberResult Failure(params string[] errors) => new(false, [], errors);
}

/// <summary>Outcome of a bulk kind change: how many shapes changed, and how many spot links it cost.</summary>
public sealed record MapKindChangeResult(bool Succeeded, int Changed, int Unlinked, IReadOnlyList<string> Errors)
{
    public static MapKindChangeResult Failure(params string[] errors) => new(false, 0, 0, errors);
}

/// <summary>One shape's label, as it is to be written.</summary>
public sealed record MapShapeLabel(Guid ShapeId, string? Label);

/// <summary>One shape to put back, as it was before it was deleted.</summary>
public sealed record MapShapeRestore(MapShapeKind Kind, MapRect Rect, string? Label, Guid? ParkingSpotId);

/// <summary>A map's traced-over site plan, streamed to the editor.</summary>
public sealed record MapBackgroundDto(byte[] Content, string ContentType);

/// <summary>
/// Outcome of a create that the caller needs the result of. Unlike <see cref="ParkingResult"/>,
/// success carries the shapes that were written — the editor selects them straight away.
/// </summary>
public sealed record MapShapeResult(bool Succeeded, IReadOnlyList<MapShapeDto> Shapes, IReadOnlyList<string> Errors)
{
    public static MapShapeResult Success(IReadOnlyList<MapShapeDto> shapes) => new(true, shapes, []);

    public static MapShapeResult Failure(params string[] errors) => new(false, [], errors);
}

/// <summary>
/// Outcome of a geometry batch: the geometry as stored, so the canvas can be corrected in place
/// without re-reading the whole drawing after every drag.
/// </summary>
public sealed record MapMoveResult(bool Succeeded, IReadOnlyList<ShapeGeometryUpdate> Stored, IReadOnlyList<string> Errors)
{
    public static MapMoveResult Success(IReadOnlyList<ShapeGeometryUpdate> stored) => new(true, stored, []);

    public static MapMoveResult Failure(params string[] errors) => new(false, [], errors);
}
