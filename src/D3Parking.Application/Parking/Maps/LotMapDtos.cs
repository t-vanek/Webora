using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Maps;

namespace D3Parking.Application.Parking.Maps;

/// <summary>One map in the chooser: what it is and how far along the tracing is.</summary>
public sealed record LotMapSummaryDto(
    Guid Id,
    string Name,
    int Width,
    int Height,
    bool IsPublished,
    bool HasBackground,
    int ShapeCount,
    /// <summary>Stall shapes bound to a real spot — the ones a live board can colour and make clickable.</summary>
    int LinkedCount,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// One drawn shape as the editor sees it. The spot columns are denormalized on purpose: the editor
/// draws five hundred shapes at once and must not resolve a code per shape.
/// </summary>
public sealed record MapShapeDto(
    Guid Id,
    MapShapeKind Kind,
    string? Label,
    double X,
    double Y,
    double Width,
    double Height,
    double Rotation,
    Guid? ParkingSpotId,
    string? SpotCode,
    ParkingSpotType? SpotType,
    bool SpotIsActive)
{
    public MapRect Rect => new(X, Y, Width, Height, Rotation);

    /// <summary>
    /// Whether the label and the linked spot's code disagree — the plan was renumbered, or the wrong
    /// rectangle got linked. Worth showing, because nothing else would ever surface it.
    /// </summary>
    public bool LabelMismatch =>
        SpotCode is not null && !string.IsNullOrWhiteSpace(Label)
        && !string.Equals(SpotCode, Label.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A map and everything drawn on it — one round trip, which is how the editor opens.</summary>
public sealed record LotMapDetailDto(
    Guid Id,
    string Name,
    int Width,
    int Height,
    int GridSize,
    bool IsPublished,
    bool HasBackground,
    int BackgroundOpacity,
    IReadOnlyList<MapShapeDto> Shapes);

/// <summary>
/// A shape's new geometry after a drag. Sent in batches: moving a selection of forty stalls is one
/// message and one transaction, not forty.
/// </summary>
public sealed record ShapeGeometryUpdate(
    Guid ShapeId,
    double X,
    double Y,
    double Width,
    double Height,
    double Rotation)
{
    public MapRect ToRect() => new(X, Y, Width, Height, Rotation);
}

/// <summary>What a row request asks for, beyond the shape it repeats.</summary>
public sealed record MapRowRequest(
    Guid SourceShapeId,
    int Count,
    double Gap,
    RowDirection Direction,
    int LabelStep = 1);

/// <summary>
/// The outcome of matching shape labels against spot codes. Both unmatched lists are reported, since
/// they mean different things: a label with no spot is a stall the company does not rent (or is not
/// created yet), while a spot with no label is a spot whose rectangle nobody has drawn.
/// </summary>
public sealed record MapAutoLinkResult(
    int Linked,
    int AlreadyLinked,
    IReadOnlyList<string> UnmatchedLabels,
    IReadOnlyList<string> UnmatchedCodes);

/// <summary>The outcome of turning drawn rectangles into real, bookable spots.</summary>
public sealed record MapSpotCreationResult(
    bool Succeeded,
    int CreatedCount,
    /// <summary>Labels skipped because a spot with that code already existed; those shapes were linked to it.</summary>
    IReadOnlyList<string> LinkedToExisting,
    /// <summary>Shapes skipped because they carry no label to name a spot after.</summary>
    int UnlabelledCount,
    IReadOnlyList<string> Errors)
{
    public static MapSpotCreationResult Failure(params string[] errors) => new(false, 0, [], 0, errors);
}
