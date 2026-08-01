using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Parking;
using D3Parking.Application.Parking.Maps;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Maps;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

/// <summary>
/// Storage and editing of lot maps. Shapes are read and written as their own rows rather than through
/// the map, because the editor's hot path is "move these forty rectangles" — an aggregate load of the
/// whole drawing per drag would make a 500-stall plan unusable.
/// </summary>
public sealed class LotMapService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    TimeProvider timeProvider) : ILotMapService
{
    /// <summary>Upper bound on one geometry batch: the largest selection a drag can reasonably carry.</summary>
    private const int MaxBatchUpdates = 1_000;

    /// <summary>Mirrors the ParkingSpots.Code column config in D3ParkingDbContext.</summary>
    private const int MaxSpotCodeLength = 32;

    /// <summary>
    /// Upper bound on one JSON import. The file comes from an administrator, but "paste a file and get
    /// an unbounded number of rows" is a footgun whoever authored the file did not intend. It is the
    /// per-map cap because an import creates a whole map at once and so never passes through
    /// <see cref="IsFullAsync"/> — a looser number here would be a way to build a map the editor
    /// refuses to grow and struggles to open.
    /// </summary>
    private const int MaxImportShapes = MaxShapesPerMap;

    /// <summary>
    /// How many shapes one map may hold. The editor draws every one of them into a single SVG over a
    /// Blazor circuit, so a drawing that grows past what the circuit can repaint stops being editable —
    /// and the only way out of that would be deleting the map.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed. At 460 shapes — the size of the plan this was built for — selecting a
    /// single stall round-trips in about 420 ms and the markup is 134 KiB; both scale with the shape
    /// count, so three times that plan is already a second and a half per click and the far side of
    /// usable. 1500 leaves real headroom above any site plan we have seen while keeping the worst case
    /// something an administrator can still work in. It is a runaway guard, not a design limit: a
    /// campus that genuinely needs more wants a map per lot, which the model already allows.
    /// </remarks>
    public const int MaxShapesPerMap = 1_500;

    /// <summary>Offset for a duplicate on a map with snapping off, in map units.</summary>
    private const int DefaultDuplicateOffset = 10;

    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true };

    public async Task<IReadOnlyList<LotMapSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.LotMaps.AsNoTracking()
            .OrderByDescending(m => m.IsPublished)
            .ThenBy(m => m.Name)
            .Select(m => new LotMapSummaryDto(
                m.Id,
                m.Name,
                m.Width,
                m.Height,
                m.IsPublished,
                m.Background != null,
                dbContext.MapShapes.Count(s => s.LotMapId == m.Id),
                dbContext.MapShapes.Count(s => s.LotMapId == m.Id && s.ParkingSpotId != null),
                m.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public Task<LotMapDetailDto?> GetAsync(Guid mapId, CancellationToken cancellationToken = default) =>
        LoadDetailAsync(m => m.Id == mapId, cancellationToken);

    public Task<LotMapDetailDto?> GetPublishedAsync(CancellationToken cancellationToken = default) =>
        LoadDetailAsync(m => m.IsPublished, cancellationToken);

    private async Task<LotMapDetailDto?> LoadDetailAsync(
        System.Linq.Expressions.Expression<Func<LotMap, bool>> predicate,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.AsNoTracking()
            .Where(predicate)
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.Width,
                m.Height,
                m.GridSize,
                m.IsPublished,
                m.BackgroundOpacity,
                HasBackground = m.Background != null,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (map is null)
        {
            return null;
        }

        var rows = await dbContext.MapShapes.AsNoTracking()
            .Where(s => s.LotMapId == map.Id)
            .ToListAsync(cancellationToken);

        // Two queries and a dictionary, rather than a correlated lookup per shape: a plan is hundreds
        // of shapes and the linked spots are a handful of columns, so fetching them once is both less
        // SQL and less to get wrong than a three-subquery projection.
        var linkedIds = rows.Where(s => s.ParkingSpotId is not null).Select(s => s.ParkingSpotId!.Value).Distinct().ToList();
        var spots = linkedIds.Count == 0
            ? []
            : await dbContext.ParkingSpots.AsNoTracking()
                .Where(s => linkedIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Code, s.Type, s.IsActive })
                .ToDictionaryAsync(s => s.Id, cancellationToken);

        var shapes = rows.Select(s =>
        {
            var spot = s.ParkingSpotId is { } id && spots.TryGetValue(id, out var found) ? found : null;
            return new MapShapeDto(
                s.Id, s.Kind, s.Label, s.X, s.Y, s.Width, s.Height, s.Rotation,
                s.ParkingSpotId, spot?.Code, spot?.Type, spot?.IsActive ?? false);
        });

        return new LotMapDetailDto(
            map.Id,
            map.Name,
            map.Width,
            map.Height,
            map.GridSize,
            map.IsPublished,
            map.HasBackground,
            map.BackgroundOpacity,
            Layered(shapes));
    }

    /// <summary>
    /// Painter's order: context first, stalls over it, captions last — and within each layer, the
    /// order the shapes read. The layering is what makes the drawing legible; the reading order makes
    /// the result deterministic (a query with no ORDER BY does not promise one) and puts the DOM in
    /// roughly the order somebody looking at the map would go through it.
    /// </summary>
    private static List<MapShapeDto> Layered(IEnumerable<MapShapeDto> shapes) =>
        shapes
            .GroupBy(s => s.Kind switch
            {
                MapShapeKind.Building => 0,
                MapShapeKind.Aisle => 1,
                MapShapeKind.Spot => 2,
                _ => 3,
            })
            .OrderBy(g => g.Key)
            .SelectMany(g => MapShapeOrder.Reading(g.ToList(), s => s.Rect))
            .ToList();

    public async Task<ParkingResult> CreateAsync(string name, int width, int height, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ParkingResult.Failure("Map_Error_NameRequired");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var trimmed = name.Trim();
        if (await dbContext.LotMaps.AnyAsync(m => m.Name == trimmed, cancellationToken))
        {
            return ParkingResult.Failure("Map_Error_DuplicateName");
        }

        dbContext.LotMaps.Add(new LotMap(trimmed, width, height, timeProvider.GetUtcNow()));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
        {
            // A concurrent create took the name between the check and the save; the unique index is
            // the last line of defence, and the caller wants the same message either way.
            return ParkingResult.Failure("Map_Error_DuplicateName");
        }

        return ParkingResult.Success;
    }

    public async Task<ParkingResult> UpdateAsync(
        Guid mapId,
        string name,
        int width,
        int height,
        int gridSize,
        int backgroundOpacity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ParkingResult.Failure("Map_Error_NameRequired");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return ParkingResult.Failure("Map_Error_NotFound");
        }

        var trimmed = name.Trim();
        if (await dbContext.LotMaps.AnyAsync(m => m.Id != mapId && m.Name == trimmed, cancellationToken))
        {
            return ParkingResult.Failure("Map_Error_DuplicateName");
        }

        var wasSmaller = width < map.Width || height < map.Height;
        map.Rename(trimmed);
        map.Resize(width, height);
        map.SetGridSize(gridSize);
        map.SetBackgroundOpacity(backgroundOpacity);
        map.Touch(timeProvider.GetUtcNow());

        // Making the map smaller would otherwise leave whatever sat beyond the new edge off the
        // canvas: unclickable, unreachable by zoom, and invisible. Sliding those shapes back in is
        // the only outcome that keeps the drawing editable.
        if (wasSmaller)
        {
            var shapes = await dbContext.MapShapes.Where(s => s.LotMapId == mapId).ToListAsync(cancellationToken);
            foreach (var shape in shapes)
            {
                var clamped = shape.Rect.ClampedInto(map.Width, map.Height);
                if (clamped != shape.Rect)
                {
                    shape.SetRect(clamped);
                }
            }
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
        {
            return ParkingResult.Failure("Map_Error_DuplicateName");
        }

        return ParkingResult.Success;
    }

    public async Task<ParkingResult> SetPublishedAsync(Guid mapId, bool published, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return ParkingResult.Failure("Map_Error_NotFound");
        }

        map.Touch(timeProvider.GetUtcNow());

        if (!published)
        {
            map.Unpublish();
            await dbContext.SaveChangesAsync(cancellationToken);
            return ParkingResult.Success;
        }

        // One published map at a time. The two writes cannot go in one SaveChanges: EF batches them
        // and the filtered unique index is checked per statement, so whichever order the batch happens
        // to take can hit "two published rows" half-way through — which it does. Ordered explicitly
        // instead, inside one transaction, so there is never an instant with two nor one with none.
        var others = await dbContext.LotMaps.Where(m => m.Id != mapId && m.IsPublished).ToListAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var other in others)
        {
            other.Unpublish();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        map.Publish();
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> DeleteAsync(Guid mapId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return ParkingResult.Failure("Map_Error_NotFound");
        }

        // Shapes go with it by cascade; no spot is touched — deleting a drawing must never delete
        // the lot it drew.
        dbContext.LotMaps.Remove(map);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> SetBackgroundAsync(Guid mapId, byte[] content, CancellationToken cancellationToken = default)
    {
        if (content.Length == 0)
        {
            return ParkingResult.Failure("Map_Error_BackgroundEmpty");
        }

        if (content.Length > LotMap.MaxBackgroundBytes)
        {
            return ParkingResult.Failure("Map_Error_BackgroundTooLarge");
        }

        // The type comes from the bytes, never from the upload. What is stored here is what the
        // endpoint later serves back from this application's own origin.
        var detected = ImageContentType.Detect(content);
        if (detected is null)
        {
            return ParkingResult.Failure("Map_Error_BackgroundNotAnImage");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return ParkingResult.Failure("Map_Error_NotFound");
        }

        map.SetBackground(content, detected);
        map.Touch(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> ClearBackgroundAsync(Guid mapId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return ParkingResult.Failure("Map_Error_NotFound");
        }

        map.ClearBackground();
        map.Touch(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<MapBackgroundDto?> GetBackgroundAsync(Guid mapId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = await dbContext.LotMaps.AsNoTracking()
            .Where(m => m.Id == mapId && m.Background != null)
            .Select(m => new { m.Background, m.BackgroundContentType })
            .FirstOrDefaultAsync(cancellationToken);

        return row?.Background is null
            ? null
            : new MapBackgroundDto(row.Background, row.BackgroundContentType ?? "application/octet-stream");
    }

    public async Task<MapShapeResult> AddShapeAsync(
        Guid mapId,
        MapShapeKind kind,
        MapRect rect,
        string? label,
        CancellationToken cancellationToken = default)
    {
        var sane = rect.Sanitized();
        if (!sane.IsValid())
        {
            return MapShapeResult.Failure("Map_Error_ShapeGeometry");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return MapShapeResult.Failure("Map_Error_NotFound");
        }

        if (await IsFullAsync(dbContext, mapId, 1, cancellationToken))
        {
            return MapShapeResult.Failure("Map_Error_MapFull");
        }

        var shape = new MapShape(mapId, kind, sane.ClampedInto(map.Width, map.Height).Sanitized(), label);
        dbContext.MapShapes.Add(shape);
        map.Touch(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapShapeResult.Success([ToDto(shape)]);
    }

    public async Task<MapShapeResult> AddRowAsync(Guid mapId, MapRowRequest request, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return MapShapeResult.Failure("Map_Error_NotFound");
        }

        var source = await dbContext.MapShapes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.SourceShapeId && s.LotMapId == mapId, cancellationToken);
        if (source is null)
        {
            return MapShapeResult.Failure("Map_Error_ShapeNotFound");
        }

        var expansion = MapRowPlan.Expand(
            source.Rect, source.Label, request.Count, request.Gap, request.Direction, request.LabelStep);
        if (!expansion.Succeeded)
        {
            return MapShapeResult.Failure(expansion.ErrorKey!);
        }

        // Index 0 is the source, which already exists — everything after it is what the row adds.
        // Clamped as it goes: a row that runs off the edge stacks up against it rather than laying
        // stalls down where nobody can ever click them again.
        var created = expansion.Shapes.Skip(1)
            .Select(planned => new MapShape(
                mapId, source.Kind, planned.Rect.ClampedInto(map.Width, map.Height).Sanitized(), planned.Label))
            .ToList();

        if (created.Count == 0)
        {
            return MapShapeResult.Success([]);
        }

        if (await IsFullAsync(dbContext, mapId, created.Count, cancellationToken))
        {
            return MapShapeResult.Failure("Map_Error_MapFull");
        }

        dbContext.MapShapes.AddRange(created);
        map.Touch(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapShapeResult.Success(created.Select(ToDto).ToList());
    }

    public async Task<ParkingResult> MatchToBackgroundAsync(
        Guid mapId,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken = default)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return ParkingResult.Failure("Map_Error_BackgroundSizeUnknown");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return ParkingResult.Failure("Map_Error_NotFound");
        }

        await ReshapeCanvasAsync(dbContext, map, imageWidth, imageHeight, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    /// <summary>
    /// Puts the map into a coordinate space of the given proportions, scaling every shape already on
    /// it so a drawing keeps sitting where it sat. Returns the factors applied, so a caller placing
    /// new geometry can land it in the same frame. Nothing is saved here.
    /// </summary>
    /// <remarks>
    /// A rotated rectangle under a non-uniform scale is not exactly a rectangle any more, so its
    /// extents are scaled per axis and the angle kept — an approximation that only bites when the
    /// aspect ratio itself changes, which is a correction worth making early and rarely later.
    /// </remarks>
    private async Task<(double Fx, double Fy)> ReshapeCanvasAsync(
        D3ParkingDbContext dbContext,
        LotMap map,
        double sourceWidth,
        double sourceHeight,
        CancellationToken cancellationToken)
    {
        // Shrunk uniformly if the source is enormous — the ratio is the whole point, so it must
        // survive the shrink.
        var scale = Math.Min(1d, LotMap.MaxDimension / Math.Max(sourceWidth, sourceHeight));
        var width = Math.Clamp((int)Math.Round(sourceWidth * scale), LotMap.MinDimension, LotMap.MaxDimension);
        var height = Math.Clamp((int)Math.Round(sourceHeight * scale), LotMap.MinDimension, LotMap.MaxDimension);

        var (fx, fy) = ((double)width / map.Width, (double)height / map.Height);
        if (width == map.Width && height == map.Height)
        {
            return (1, 1);
        }

        map.Resize(width, height);

        // The grid is a length in map units, so resizing the coordinate space changes what it means.
        // Left alone it silently becomes wrong by the scale factor: a 1200-wide map with a grid of 5
        // matched to a 400-wide photo keeps a grid of 5, which is now three times coarser against the
        // drawing than the one that was chosen — and on a small underlay that is half a stall, so
        // snapping fights the tracing instead of helping it. The geometric mean is the one number
        // that fits a grid applied to both axes when they scale differently. Zero means snapping is
        // off and must stay off; one is the floor, since a grid of zero would turn it off by rounding.
        if (map.GridSize > 0)
        {
            map.SetGridSize(Math.Max(1, (int)Math.Round(map.GridSize * Math.Sqrt(fx * fy))));
        }

        map.Touch(timeProvider.GetUtcNow());

        var shapes = await dbContext.MapShapes.Where(sh => sh.LotMapId == map.Id).ToListAsync(cancellationToken);
        foreach (var shape in shapes)
        {
            var scaled = new MapRect(shape.X * fx, shape.Y * fy, shape.Width * fx, shape.Height * fy, shape.Rotation)
                .Sanitized()
                .ClampedInto(width, height)
                .Sanitized();
            if (scaled.IsValid())
            {
                shape.SetRect(scaled);
            }
        }

        return (fx, fy);
    }

    public async Task<MapSvgImportResult> ImportSvgAsync(
        Guid mapId,
        string svg,
        MapShapeKind kind,
        CancellationToken cancellationToken = default)
    {
        var reading = SvgPlanReader.Read(svg);
        if (!reading.Succeeded)
        {
            return MapSvgImportResult.Failure(reading.ErrorKey!);
        }

        if (reading.Shapes.Count == 0)
        {
            return MapSvgImportResult.Failure("Map_Error_SvgNoShapes");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return MapSvgImportResult.Failure("Map_Error_NotFound");
        }

        if (await IsFullAsync(dbContext, mapId, reading.Shapes.Count, cancellationToken))
        {
            return MapSvgImportResult.Failure("Map_Error_MapFull");
        }

        // The map takes the drawing's own proportions, and whatever was already on it comes along.
        // The plan then lands at its native coordinates times the same factor, so an import onto a
        // half-traced map lines up with what is there instead of on top of it.
        await ReshapeCanvasAsync(dbContext, map, reading.Width, reading.Height, cancellationToken);

        // Uniform, and the two ratios are equal by construction: the map has just been given the
        // drawing's proportions. Taking the smaller of the two is what keeps that true after the
        // clamp to the coordinate space's limits rounds one of them.
        var scale = Math.Min(map.Width / reading.Width, map.Height / reading.Height);

        var created = new List<MapShape>(reading.Shapes.Count);
        foreach (var planned in reading.Shapes)
        {
            var rect = new MapRect(
                    planned.Rect.X * scale, planned.Rect.Y * scale,
                    planned.Rect.Width * scale, planned.Rect.Height * scale,
                    planned.Rect.Rotation)
                .Sanitized()
                .ClampedInto(map.Width, map.Height)
                .Sanitized();
            if (!rect.IsValid())
            {
                continue;
            }

            created.Add(new MapShape(mapId, kind, rect, planned.Label));
        }

        if (created.Count == 0)
        {
            return MapSvgImportResult.Failure("Map_Error_SvgNoShapes");
        }

        dbContext.MapShapes.AddRange(created);
        map.Touch(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MapSvgImportResult(
            true,
            created.Count,
            created.Count(sh => sh.Label is not null),
            created.Select(sh => sh.Id).ToList(),
            reading.Warnings,
            []);
    }

    public async Task<MapShapeResult> DuplicateShapesAsync(
        Guid mapId,
        IReadOnlyList<Guid> shapeIds,
        CancellationToken cancellationToken = default)
    {
        if (shapeIds.Count == 0)
        {
            return MapShapeResult.Success([]);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return MapShapeResult.Failure("Map_Error_NotFound");
        }

        var ids = shapeIds.ToList();
        var originals = await dbContext.MapShapes.AsNoTracking()
            .Where(sh => sh.LotMapId == mapId && ids.Contains(sh.Id))
            .ToListAsync(cancellationToken);

        if (originals.Count == 0)
        {
            return MapShapeResult.Success([]);
        }

        if (await IsFullAsync(dbContext, mapId, originals.Count, cancellationToken))
        {
            return MapShapeResult.Failure("Map_Error_MapFull");
        }

        // Offset by a quarter of the selection's smaller side, never less than a grid step. Tied to
        // the shapes rather than to the grid: a grid step is a handful of units, and on a stall two
        // hundred units wide that puts the copy all but exactly on top of the original — where the
        // next click lands on whichever the browser decides is on top.
        var bounds = originals.Select(sh => sh.Rect.Bounds()).ToList();
        var extent = Math.Min(
            bounds.Max(b => b.MaxX) - bounds.Min(b => b.MinX),
            bounds.Max(b => b.MaxY) - bounds.Min(b => b.MinY));
        var offset = Math.Max(map.GridSize > 0 ? map.GridSize : DefaultDuplicateOffset, Math.Round(extent / 4));

        // Reading order, so duplicating a row hands the copies back in the order the row reads. A
        // query without an ORDER BY returns whatever the server finds convenient, which makes the
        // result of this call quietly depend on the plan it happened to pick.
        var copies = MapShapeOrder.Reading(originals, sh => sh.Rect)
            .Select(sh => new MapShape(
                mapId,
                sh.Kind,
                sh.Rect.MovedBy(offset, offset).Sanitized().ClampedInto(map.Width, map.Height).Sanitized(),
                sh.Label))
            .Where(sh => sh.Rect.IsValid())
            .ToList();

        // No spot link is copied: a spot is drawn by exactly one rectangle, and a duplicate is a
        // second rectangle. Auto-link will not claim it either, since the original still holds it.
        dbContext.MapShapes.AddRange(copies);
        map.Touch(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapShapeResult.Success(copies.Select(ToDto).ToList());
    }

    public async Task<MapRenumberResult> RenumberShapesAsync(
        Guid mapId,
        IReadOnlyList<Guid> shapeIds,
        string firstLabel,
        int step,
        bool reverse,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(firstLabel))
        {
            return MapRenumberResult.Failure("Map_Error_RenumberStart");
        }

        if (shapeIds.Count == 0)
        {
            return MapRenumberResult.Success([]);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ids = shapeIds.ToList();
        var shapes = await dbContext.MapShapes
            .Where(sh => sh.LotMapId == mapId && ids.Contains(sh.Id))
            .ToListAsync(cancellationToken);

        if (shapes.Count == 0)
        {
            return MapRenumberResult.Success([]);
        }

        var ordered = MapShapeOrder.Reading(shapes, sh => sh.Rect, reverse);
        var labels = new List<string>(ordered.Count);
        string? label = firstLabel.Trim();

        foreach (var shape in ordered)
        {
            if (label is null)
            {
                // The start had no trailing number, so there is no second label to give. The first
                // shape is still named; the rest keep whatever they had.
                break;
            }

            shape.Relabel(label);
            labels.Add(label);
            label = MapLabelSequence.Next(label, step);
        }

        await TouchMapAsync(dbContext, mapId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapRenumberResult.Success(labels);
    }

    public async Task<ParkingResult> SetLabelsAsync(
        Guid mapId,
        IReadOnlyList<MapShapeLabel> labels,
        CancellationToken cancellationToken = default)
    {
        if (labels.Count == 0)
        {
            return ParkingResult.Success;
        }

        if (labels.Count > MaxBatchUpdates)
        {
            return ParkingResult.Failure("Map_Error_BatchTooLarge");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var wanted = labels.GroupBy(l => l.ShapeId).ToDictionary(g => g.Key, g => g.Last().Label);
        var ids = wanted.Keys.ToList();
        var shapes = await dbContext.MapShapes
            .Where(sh => sh.LotMapId == mapId && ids.Contains(sh.Id))
            .ToListAsync(cancellationToken);

        foreach (var shape in shapes)
        {
            shape.Relabel(wanted[shape.Id]);
        }

        await TouchMapAsync(dbContext, mapId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<MapKindChangeResult> SetKindAsync(
        Guid mapId,
        IReadOnlyList<Guid> shapeIds,
        MapShapeKind kind,
        CancellationToken cancellationToken = default)
    {
        if (shapeIds.Count == 0)
        {
            return new MapKindChangeResult(true, 0, 0, []);
        }

        if (shapeIds.Count > MaxBatchUpdates)
        {
            return MapKindChangeResult.Failure("Map_Error_BatchTooLarge");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ids = shapeIds.ToList();
        var shapes = await dbContext.MapShapes
            .Where(sh => sh.LotMapId == mapId && ids.Contains(sh.Id))
            .ToListAsync(cancellationToken);

        var changed = 0;
        var unlinked = 0;
        foreach (var shape in shapes.Where(sh => sh.Kind != kind))
        {
            // ChangeKind clears the spot link for anything that is not a stall; counting it here is
            // what lets the editor say so rather than leaving it to be noticed later.
            if (shape.IsLinked && kind != MapShapeKind.Spot)
            {
                unlinked++;
            }

            shape.ChangeKind(kind);
            changed++;
        }

        if (changed > 0)
        {
            await TouchMapAsync(dbContext, mapId, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new MapKindChangeResult(true, changed, unlinked, []);
    }

    public async Task<ParkingResult> UpdateShapeAsync(
        Guid shapeId,
        string? label,
        MapShapeKind kind,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var shape = await dbContext.MapShapes.FirstOrDefaultAsync(s => s.Id == shapeId, cancellationToken);
        if (shape is null)
        {
            return ParkingResult.Failure("Map_Error_ShapeNotFound");
        }

        shape.Relabel(label);
        shape.ChangeKind(kind);
        await TouchMapAsync(dbContext, shape.LotMapId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<MapMoveResult> MoveShapesAsync(
        Guid mapId,
        IReadOnlyList<ShapeGeometryUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0)
        {
            return MapMoveResult.Success([]);
        }

        if (updates.Count > MaxBatchUpdates)
        {
            return MapMoveResult.Failure("Map_Error_BatchTooLarge");
        }

        // Validated before anything is loaded: these numbers came from a pointer drag in a browser, and
        // a single NaN in the batch must not leave half the selection moved.
        var wanted = new Dictionary<Guid, MapRect>(updates.Count);
        foreach (var update in updates)
        {
            var rect = update.ToRect().Sanitized();
            if (!rect.IsValid())
            {
                return MapMoveResult.Failure("Map_Error_ShapeGeometry");
            }

            wanted[update.ShapeId] = rect;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return MapMoveResult.Failure("Map_Error_NotFound");
        }

        var ids = wanted.Keys.ToList();
        var shapes = await dbContext.MapShapes
            .Where(s => s.LotMapId == mapId && ids.Contains(s.Id))
            .ToListAsync(cancellationToken);

        var stored = new List<ShapeGeometryUpdate>(shapes.Count);
        foreach (var shape in shapes)
        {
            shape.SetRect(wanted[shape.Id].ClampedInto(map.Width, map.Height).Sanitized());
            stored.Add(new ShapeGeometryUpdate(shape.Id, shape.X, shape.Y, shape.Width, shape.Height, shape.Rotation));
        }

        map.Touch(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        // What was stored, not what was asked for: the geometry may have been clamped back onto the
        // map, and the canvas has to show the truth rather than the browser's proposal.
        return MapMoveResult.Success(stored);
    }

    public async Task<ParkingResult> DeleteShapesAsync(Guid mapId, IReadOnlyList<Guid> shapeIds, CancellationToken cancellationToken = default)
    {
        if (shapeIds.Count == 0)
        {
            return ParkingResult.Success;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ids = shapeIds.ToList();
        var shapes = await dbContext.MapShapes
            .Where(s => s.LotMapId == mapId && ids.Contains(s.Id))
            .ToListAsync(cancellationToken);

        if (shapes.Count == 0)
        {
            return ParkingResult.Success;
        }

        dbContext.MapShapes.RemoveRange(shapes);
        await TouchMapAsync(dbContext, mapId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<MapShapeResult> RestoreShapesAsync(
        Guid mapId,
        IReadOnlyList<MapShapeRestore> shapes,
        CancellationToken cancellationToken = default)
    {
        if (shapes.Count == 0)
        {
            return MapShapeResult.Success([]);
        }

        if (shapes.Count > MaxBatchUpdates)
        {
            return MapShapeResult.Failure("Map_Error_BatchTooLarge");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return MapShapeResult.Failure("Map_Error_NotFound");
        }

        // Which spots are still undrawn, and which still exist at all. A restore must not resurrect a
        // link to a spot that has since been retired or drawn by somebody else's rectangle — the
        // shape comes back either way, just unlinked.
        var wanted = shapes.Where(s => s.ParkingSpotId is not null).Select(s => s.ParkingSpotId!.Value).Distinct().ToList();
        var free = new HashSet<Guid>();
        if (wanted.Count > 0)
        {
            var live = await dbContext.ParkingSpots.Where(s => wanted.Contains(s.Id)).Select(s => s.Id).ToListAsync(cancellationToken);
            var drawn = await dbContext.MapShapes
                .Where(s => s.ParkingSpotId != null && wanted.Contains(s.ParkingSpotId!.Value))
                .Select(s => s.ParkingSpotId!.Value)
                .ToListAsync(cancellationToken);
            free = live.Except(drawn).ToHashSet();
        }

        var restored = new List<MapShape>(shapes.Count);
        foreach (var wantedShape in shapes)
        {
            var rect = wantedShape.Rect.Sanitized().ClampedInto(map.Width, map.Height).Sanitized();
            if (!rect.IsValid())
            {
                continue;
            }

            var shape = new MapShape(mapId, wantedShape.Kind, rect, wantedShape.Label);
            if (wantedShape.Kind == MapShapeKind.Spot
                && wantedShape.ParkingSpotId is { } spotId
                && free.Remove(spotId))
            {
                shape.LinkSpot(spotId);
            }

            restored.Add(shape);
        }

        if (restored.Count == 0)
        {
            return MapShapeResult.Success([]);
        }

        dbContext.MapShapes.AddRange(restored);
        map.Touch(timeProvider.GetUtcNow());
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
        {
            return MapShapeResult.Failure("Map_Error_SpotAlreadyDrawn");
        }

        return MapShapeResult.Success(restored.Select(ToDto).ToList());
    }

    public async Task<ParkingResult> LinkSpotAsync(Guid shapeId, Guid? spotId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var shape = await dbContext.MapShapes.FirstOrDefaultAsync(s => s.Id == shapeId, cancellationToken);
        if (shape is null)
        {
            return ParkingResult.Failure("Map_Error_ShapeNotFound");
        }

        if (spotId is { } id)
        {
            if (shape.Kind != MapShapeKind.Spot)
            {
                return ParkingResult.Failure("Map_Error_LinkNotASpotShape");
            }

            if (!await dbContext.ParkingSpots.AnyAsync(s => s.Id == id, cancellationToken))
            {
                return ParkingResult.Failure("Parking_Error_SpotNotFound");
            }

            // One rectangle per spot: two shapes claiming spot 434 would make "where is 434" depend on
            // row order, and the unique index behind this is the backstop for the same reason.
            var taken = await dbContext.MapShapes
                .AnyAsync(s => s.Id != shapeId && s.ParkingSpotId == id, cancellationToken);
            if (taken)
            {
                return ParkingResult.Failure("Map_Error_SpotAlreadyDrawn");
            }
        }

        shape.LinkSpot(spotId);
        await TouchMapAsync(dbContext, shape.LotMapId, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
        {
            // Another editor drew the same spot between the check above and this save.
            return ParkingResult.Failure("Map_Error_SpotAlreadyDrawn");
        }

        return ParkingResult.Success;
    }

    public async Task<MapAutoLinkResult> AutoLinkAsync(Guid mapId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var shapes = await dbContext.MapShapes
            .Where(s => s.LotMapId == mapId && s.Kind == MapShapeKind.Spot)
            .ToListAsync(cancellationToken);

        var spots = await dbContext.ParkingSpots.AsNoTracking()
            .Select(s => new { s.Id, s.Code })
            .ToListAsync(cancellationToken);

        var byCode = spots
            .GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        // Spots already drawn anywhere — including on another map — are not offered again, so running
        // this twice is a no-op rather than a fight over the same rectangle.
        var alreadyDrawn = await dbContext.MapShapes
            .Where(s => s.ParkingSpotId != null)
            .Select(s => s.ParkingSpotId!.Value)
            .ToListAsync(cancellationToken);
        var claimed = alreadyDrawn.ToHashSet();

        var linked = 0;
        var alreadyLinked = 0;
        var unmatchedLabels = new List<string>();

        foreach (var shape in shapes)
        {
            if (shape.IsLinked)
            {
                alreadyLinked++;
                continue;
            }

            var label = shape.Label?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            if (byCode.TryGetValue(label, out var spotId) && claimed.Add(spotId))
            {
                shape.LinkSpot(spotId);
                linked++;
            }
            else
            {
                unmatchedLabels.Add(label);
            }
        }

        if (linked > 0)
        {
            await TouchMapAsync(dbContext, mapId, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var unmatchedCodes = spots
            .Where(s => !claimed.Contains(s.Id))
            .Select(s => s.Code)
            .OrderBy(c => c, SpotCodeComparer.Instance)
            .ToList();

        return new MapAutoLinkResult(
            linked,
            alreadyLinked,
            unmatchedLabels.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, SpotCodeComparer.Instance).ToList(),
            unmatchedCodes);
    }

    public async Task<MapSpotCreationResult> CreateSpotsFromShapesAsync(
        Guid mapId,
        IReadOnlyList<Guid> shapeIds,
        ParkingSpotType type,
        CancellationToken cancellationToken = default)
    {
        if (shapeIds.Count == 0)
        {
            return new MapSpotCreationResult(true, 0, [], 0, [], []);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var ids = shapeIds.ToList();
        var shapes = await dbContext.MapShapes
            .Where(s => s.LotMapId == mapId && ids.Contains(s.Id) && s.Kind == MapShapeKind.Spot)
            .ToListAsync(cancellationToken);

        if (shapes.Count == 0)
        {
            return MapSpotCreationResult.Failure("Map_Error_ShapeNotFound");
        }

        var existing = await dbContext.ParkingSpots
            .Select(s => new { s.Id, s.Code })
            .ToListAsync(cancellationToken);
        var byCode = existing
            .GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var claimed = (await dbContext.MapShapes
                .Where(s => s.ParkingSpotId != null)
                .Select(s => s.ParkingSpotId!.Value)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var created = 0;
        var unlabelled = 0;
        var linkedToExisting = new List<string>();
        var tooLong = new List<string>();

        foreach (var shape in shapes)
        {
            if (shape.IsLinked)
            {
                continue;
            }

            var label = shape.Label?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                unlabelled++;
                continue;
            }

            // A shape label may be longer than a spot code column. Reported rather than truncated:
            // silently renaming somebody's stall to a prefix of itself is worse than refusing it.
            if (label.Length > MaxSpotCodeLength)
            {
                tooLong.Add(label);
                continue;
            }

            if (byCode.TryGetValue(label, out var spotId))
            {
                // The code is taken. Linking to it is what the admin meant — re-running over a lot that
                // is half built must not fail on the half that already exists.
                if (claimed.Add(spotId))
                {
                    shape.LinkSpot(spotId);
                    linkedToExisting.Add(label);
                }

                continue;
            }

            var spot = new ParkingSpot(label, type);
            dbContext.ParkingSpots.Add(spot);
            shape.LinkSpot(spot.Id);
            byCode[label] = spot.Id;
            claimed.Add(spot.Id);
            created++;
        }

        await TouchMapAsync(dbContext, mapId, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
        {
            // Somebody created a spot with one of these codes, or drew one of these spots, while
            // this ran. Nothing committed; re-running picks up their work and links to it.
            return MapSpotCreationResult.Failure("Map_Error_SpotsRaced");
        }

        return new MapSpotCreationResult(
            true,
            created,
            linkedToExisting.OrderBy(c => c, SpotCodeComparer.Instance).ToList(),
            unlabelled,
            tooLong.OrderBy(c => c, SpotCodeComparer.Instance).ToList(),
            []);
    }

    public async Task<string?> ExportAsync(Guid mapId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await dbContext.LotMaps.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        if (map is null)
        {
            return null;
        }

        var shapes = await dbContext.MapShapes.AsNoTracking()
            .Where(s => s.LotMapId == mapId)
            .Select(s => new MapShapeExport(s.Kind.ToString(), s.Label, s.X, s.Y, s.Width, s.Height, s.Rotation))
            .ToListAsync(cancellationToken);

        // Geometry and labels only. Spot links are ids of rows that do not exist in the target
        // database, and the background is megabytes of scan — both are re-established there instead
        // (auto-link does the first in one click).
        return JsonSerializer.Serialize(
            new LotMapExport(1, map.Name, map.Width, map.Height, map.GridSize, shapes), ExportJson);
    }

    public async Task<ParkingResult> ImportAsync(string name, string json, CancellationToken cancellationToken = default)
    {
        LotMapExport? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LotMapExport>(json);
        }
        catch (JsonException)
        {
            return ParkingResult.Failure("Map_Error_ImportUnreadable");
        }

        if (payload is null || payload.Version != 1)
        {
            return ParkingResult.Failure("Map_Error_ImportUnreadable");
        }

        var mapName = string.IsNullOrWhiteSpace(name) ? payload.Name : name.Trim();
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return ParkingResult.Failure("Map_Error_NameRequired");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await dbContext.LotMaps.AnyAsync(m => m.Name == mapName, cancellationToken))
        {
            return ParkingResult.Failure("Map_Error_DuplicateName");
        }

        if ((payload.Shapes?.Count ?? 0) > MaxImportShapes)
        {
            return ParkingResult.Failure("Map_Error_ImportTooLarge");
        }

        var map = new LotMap(mapName, payload.Width, payload.Height, timeProvider.GetUtcNow());
        map.SetGridSize(payload.GridSize);
        dbContext.LotMaps.Add(map);

        foreach (var shape in payload.Shapes ?? [])
        {
            var rect = new MapRect(shape.X, shape.Y, shape.Width, shape.Height, shape.Rotation)
                .Sanitized()
                .ClampedInto(map.Width, map.Height)
                .Sanitized();
            if (!rect.IsValid())
            {
                // One unusable rectangle is not a reason to refuse the other four hundred.
                continue;
            }

            var kind = Enum.TryParse<MapShapeKind>(shape.Kind, ignoreCase: true, out var parsed)
                ? parsed
                : MapShapeKind.Spot;
            dbContext.MapShapes.Add(new MapShape(map.Id, kind, rect, shape.Label));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    /// <summary>Whether adding <paramref name="adding"/> more shapes would push the map past its cap.</summary>
    private static async Task<bool> IsFullAsync(D3ParkingDbContext dbContext, Guid mapId, int adding, CancellationToken cancellationToken) =>
        await dbContext.MapShapes.CountAsync(s => s.LotMapId == mapId, cancellationToken) + adding > MaxShapesPerMap;

    /// <summary>
    /// Stamps the map as edited without loading it. Every shape write goes through here, so "last
    /// changed" means the drawing rather than only its name and size.
    /// </summary>
    private async Task TouchMapAsync(D3ParkingDbContext dbContext, Guid mapId, CancellationToken cancellationToken)
    {
        var map = await dbContext.LotMaps.FirstOrDefaultAsync(m => m.Id == mapId, cancellationToken);
        map?.Touch(timeProvider.GetUtcNow());
    }

    /// <summary>A freshly written shape, before any spot has been linked to it.</summary>
    private static MapShapeDto ToDto(MapShape shape) => new(
        shape.Id,
        shape.Kind,
        shape.Label,
        shape.X,
        shape.Y,
        shape.Width,
        shape.Height,
        shape.Rotation,
        shape.ParkingSpotId,
        null,
        null,
        false);

    private sealed record LotMapExport(
        int Version,
        string Name,
        int Width,
        int Height,
        int GridSize,
        IReadOnlyList<MapShapeExport>? Shapes);

    private sealed record MapShapeExport(
        string Kind,
        string? Label,
        double X,
        double Y,
        double Width,
        double Height,
        double Rotation);
}
