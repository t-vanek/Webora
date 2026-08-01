using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Parking.Maps;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Maps;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins the map engine's storage rules: one published map, one rectangle per spot, a row that adds
/// only what is new, a geometry batch that is all-or-nothing, and the two bridges from a drawing to
/// a lot (auto-link and create-spots) staying safe to run twice. Requires
/// ConnectionStrings__SqlServer (skipped without it) — the filtered unique indexes these rely on are
/// the point, and no in-memory provider has them.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LotMapServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private DbContextOptions<D3ParkingDbContext> _options = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the map tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "D3Parking_LotMapTests" };
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(builder.ConnectionString).Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();
    }

    [SetUp]
    public async Task ResetAsync()
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        dbContext.MapShapes.RemoveRange(dbContext.MapShapes);
        dbContext.LotMaps.RemoveRange(dbContext.LotMaps);
        dbContext.ParkingSpots.RemoveRange(dbContext.ParkingSpots);
        await dbContext.SaveChangesAsync();
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_options is not null)
        {
            await using var dbContext = new D3ParkingDbContext(_options);
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task Publishing_a_map_withdraws_whatever_was_published_before()
    {
        var service = CreateService();
        var first = await CreateMapAsync(service, "První");
        var second = await CreateMapAsync(service, "Druhá");

        await service.SetPublishedAsync(first, true);
        await service.SetPublishedAsync(second, true);

        var published = await service.GetPublishedAsync();
        Assert.Multiple(async () =>
        {
            Assert.That(published!.Id, Is.EqualTo(second),
                "The driver-facing screens ask for one map; two published rows would make the answer depend on row order.");
            Assert.That((await service.GetAsync(first))!.IsPublished, Is.False);
        });
    }

    [Test]
    public async Task A_row_creates_everything_past_the_source_and_carries_the_numbering_on()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var source = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");

        var result = await service.AddRowAsync(mapId, new MapRowRequest(source, Count: 5, Gap: 2, RowDirection.Right));

        var map = await service.GetAsync(mapId);
        Assert.Multiple(() =>
        {
            Assert.That(result.Shapes, Has.Count.EqualTo(4), "The source already exists; a row of five adds four.");
            Assert.That(map!.Shapes.Select(s => s.Label).Order(), Is.EqualTo(new[] { "428", "429", "430", "431", "432" }));
            Assert.That(result.Shapes.Select(s => s.X), Is.EqualTo(new[] { 22d, 44d, 66d, 88d }));
        });
    }

    [Test]
    public async Task A_row_inherits_the_kind_of_the_shape_it_repeats()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var source = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "Budova", MapShapeKind.Building);

        var result = await service.AddRowAsync(mapId, new MapRowRequest(source, 3, 0, RowDirection.Down));

        Assert.That(result.Shapes.Select(s => s.Kind), Is.All.EqualTo(MapShapeKind.Building));
    }

    [Test]
    public async Task A_geometry_batch_with_one_bad_rectangle_moves_nothing()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var good = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "1");
        var other = await AddShapeAsync(service, mapId, new MapRect(50, 0, 20, 10, 0), "2");

        var result = await service.MoveShapesAsync(mapId,
        [
            new ShapeGeometryUpdate(good, 100, 100, 20, 10, 0),
            new ShapeGeometryUpdate(other, 0, 0, double.NaN, 10, 0),
        ]);

        var map = await service.GetAsync(mapId);
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(map!.Shapes.Single(s => s.Id == good).X, Is.EqualTo(0),
                "Half a drag landing is worse than none — the batch is validated before anything is loaded.");
        });
    }

    [Test]
    public async Task A_geometry_batch_ignores_ids_that_belong_to_another_map()
    {
        var service = CreateService();
        var mine = await CreateMapAsync(service, "Moje");
        var theirs = await CreateMapAsync(service, "Cizí");
        var foreign = await AddShapeAsync(service, theirs, new MapRect(0, 0, 20, 10, 0), "X");

        var result = await service.MoveShapesAsync(mine, [new ShapeGeometryUpdate(foreign, 500, 500, 20, 10, 0)]);

        var map = await service.GetAsync(theirs);
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True, "A stale editor is not an error, it is simply out of date.");
            Assert.That(map!.Shapes.Single().X, Is.EqualTo(0), "A map must not be able to move another map's shapes.");
        });
    }

    [Test]
    public async Task Auto_link_binds_matching_labels_and_reports_both_kinds_of_leftover()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");
        await AddShapeAsync(service, mapId, new MapRect(30, 0, 20, 10, 0), "429");
        // A stall of someone else's: drawn for context, never a spot of ours.
        await AddShapeAsync(service, mapId, new MapRect(60, 0, 20, 10, 0), "101");
        await CreateSpotAsync("428");
        await CreateSpotAsync("429");
        // A spot nobody has drawn a rectangle for yet.
        await CreateSpotAsync("H14");

        var report = await service.AutoLinkAsync(mapId);

        Assert.Multiple(() =>
        {
            Assert.That(report.Linked, Is.EqualTo(2));
            Assert.That(report.UnmatchedLabels, Is.EqualTo(new[] { "101" }));
            Assert.That(report.UnmatchedCodes, Is.EqualTo(new[] { "H14" }));
        });
    }

    [Test]
    public async Task Auto_link_matches_a_label_to_a_spot_code_regardless_of_case()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "h14");
        await CreateSpotAsync("H14");

        var report = await service.AutoLinkAsync(mapId);

        Assert.That(report.Linked, Is.EqualTo(1));
    }

    [Test]
    public async Task Running_auto_link_again_links_nothing_new_and_breaks_nothing()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");
        await CreateSpotAsync("428");

        await service.AutoLinkAsync(mapId);
        var second = await service.AutoLinkAsync(mapId);

        Assert.Multiple(() =>
        {
            Assert.That(second.Linked, Is.EqualTo(0));
            Assert.That(second.AlreadyLinked, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task A_spot_can_only_be_drawn_by_one_shape()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var first = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");
        var second = await AddShapeAsync(service, mapId, new MapRect(30, 0, 20, 10, 0), "428");
        var spotId = await CreateSpotAsync("428");

        await service.LinkSpotAsync(first, spotId);
        var clash = await service.LinkSpotAsync(second, spotId);

        Assert.Multiple(() =>
        {
            Assert.That(clash.Succeeded, Is.False);
            Assert.That(clash.Errors, Does.Contain("Map_Error_SpotAlreadyDrawn"));
        });
    }

    [Test]
    public async Task Only_a_stall_shape_can_stand_for_a_spot()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var building = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "Budova", MapShapeKind.Building);
        var spotId = await CreateSpotAsync("428");

        var result = await service.LinkSpotAsync(building, spotId);

        Assert.That(result.Errors, Does.Contain("Map_Error_LinkNotASpotShape"));
    }

    [Test]
    public async Task Turning_a_linked_stall_into_a_building_drops_the_link()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var shape = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");
        await service.LinkSpotAsync(shape, await CreateSpotAsync("428"));

        await service.UpdateShapeAsync(shape, "428", MapShapeKind.Building);

        var map = await service.GetAsync(mapId);
        Assert.That(map!.Shapes.Single().ParkingSpotId, Is.Null,
            "A shape the board no longer draws as clickable must not keep a live spot link.");
    }

    [Test]
    public async Task Creating_spots_from_shapes_names_them_after_the_labels_and_links_each_one()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var a = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");
        var b = await AddShapeAsync(service, mapId, new MapRect(30, 0, 20, 10, 0), "429");
        var unnamed = await AddShapeAsync(service, mapId, new MapRect(60, 0, 20, 10, 0), null);

        var result = await service.CreateSpotsFromShapesAsync(mapId, [a, b, unnamed], ParkingSpotType.Standard);

        var map = await service.GetAsync(mapId);
        await using var dbContext = new D3ParkingDbContext(_options);
        Assert.Multiple(async () =>
        {
            Assert.That(result.CreatedCount, Is.EqualTo(2));
            Assert.That(result.UnlabelledCount, Is.EqualTo(1), "A rectangle with no label has nothing to name a spot after.");
            Assert.That(map!.Shapes.Count(s => s.ParkingSpotId is not null), Is.EqualTo(2));
            Assert.That(await dbContext.ParkingSpots.CountAsync(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Creating_spots_over_a_half_built_lot_links_to_what_already_exists()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var existing = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");
        var fresh = await AddShapeAsync(service, mapId, new MapRect(30, 0, 20, 10, 0), "429");
        await CreateSpotAsync("428");

        var result = await service.CreateSpotsFromShapesAsync(mapId, [existing, fresh], ParkingSpotType.Standard);

        Assert.Multiple(() =>
        {
            Assert.That(result.CreatedCount, Is.EqualTo(1));
            Assert.That(result.LinkedToExisting, Is.EqualTo(new[] { "428" }),
                "Re-running over a lot that is half built must link rather than fail on the half that exists.");
        });
    }

    [Test]
    public async Task Deleting_a_map_takes_its_shapes_and_leaves_the_lot_alone()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var shape = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");
        await service.LinkSpotAsync(shape, await CreateSpotAsync("428"));

        await service.DeleteAsync(mapId);

        await using var dbContext = new D3ParkingDbContext(_options);
        Assert.Multiple(async () =>
        {
            Assert.That(await dbContext.MapShapes.CountAsync(), Is.EqualTo(0));
            Assert.That(await dbContext.ParkingSpots.CountAsync(), Is.EqualTo(1),
                "Deleting a drawing must never delete the lot it drew.");
        });
    }

    [Test]
    public async Task Retiring_a_spot_leaves_its_rectangle_on_the_plan_as_context()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var shape = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");
        var spotId = await CreateSpotAsync("428");
        await service.LinkSpotAsync(shape, spotId);

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            dbContext.ParkingSpots.Remove(await dbContext.ParkingSpots.SingleAsync(s => s.Id == spotId));
            await dbContext.SaveChangesAsync();
        }

        var map = await service.GetAsync(mapId);
        Assert.Multiple(() =>
        {
            Assert.That(map!.Shapes, Has.Count.EqualTo(1));
            Assert.That(map.Shapes.Single().ParkingSpotId, Is.Null, "The stall is still on the plan, just no longer ours.");
        });
    }

    [Test]
    public async Task An_exported_map_imports_back_with_the_same_geometry()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        await AddShapeAsync(service, mapId, new MapRect(10, 20, 30, 15, 45), "428");
        await AddShapeAsync(service, mapId, new MapRect(0, 0, 200, 100, 0), "Budova", MapShapeKind.Building);

        var json = await service.ExportAsync(mapId);
        var imported = await service.ImportAsync("Kopie", json!);

        var copy = (await service.ListAsync()).Single(m => m.Name == "Kopie");
        var detail = await service.GetAsync(copy.Id);
        Assert.Multiple(() =>
        {
            Assert.That(imported.Succeeded, Is.True);
            Assert.That(detail!.Shapes, Has.Count.EqualTo(2));
            var stall = detail.Shapes.Single(s => s.Kind == MapShapeKind.Spot);
            Assert.That((stall.X, stall.Y, stall.Width, stall.Height, stall.Rotation), Is.EqualTo((10d, 20d, 30d, 15d, 45d)));
            Assert.That(detail.Shapes.All(s => s.ParkingSpotId is null), Is.True,
                "Spot ids name rows that do not exist in the target database; auto-link re-establishes them there.");
        });
    }

    [Test]
    public async Task Import_refuses_a_payload_it_cannot_read()
    {
        var service = CreateService();

        var result = await service.ImportAsync("Kopie", "{ this is not json");

        Assert.That(result.Errors, Does.Contain("Map_Error_ImportUnreadable"));
    }

    [Test]
    public async Task Two_maps_cannot_share_a_name()
    {
        var service = CreateService();
        await CreateMapAsync(service, "Areál");

        var second = await service.CreateAsync("Areál", 1600, 900);

        Assert.That(second.Errors, Does.Contain("Map_Error_DuplicateName"));
    }

    // --- robustness: nothing may end up unreachable, unreadable or unrecoverable ---

    [Test]
    public async Task A_move_past_the_edge_is_clamped_back_and_the_stored_geometry_comes_home()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var shape = await AddShapeAsync(service, mapId, new MapRect(0, 0, 40, 30, 0), "1");

        var result = await service.MoveShapesAsync(mapId, [new ShapeGeometryUpdate(shape, 5_000, 5_000, 40, 30, 0)]);

        var stored = result.Stored.Single();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That((stored.X, stored.Y), Is.EqualTo((1560d, 870d)),
                "A shape dropped off the canvas cannot be clicked and the zoom cannot reach it.");
        });
    }

    [Test]
    public async Task Making_the_map_smaller_slides_the_shapes_beyond_the_new_edge_back_in()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        await AddShapeAsync(service, mapId, new MapRect(1500, 800, 40, 30, 0), "1");

        await service.UpdateAsync(mapId, "Areál", 800, 600, 5, 50);

        var shape = (await service.GetAsync(mapId))!.Shapes.Single();
        Assert.That((shape.X, shape.Y), Is.EqualTo((760d, 570d)),
            "Shrinking the map must not orphan whatever sat beyond the new edge.");
    }

    [Test]
    public async Task A_row_that_runs_off_the_edge_stacks_against_it_instead_of_laying_stalls_nowhere()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var source = await AddShapeAsync(service, mapId, new MapRect(1400, 100, 100, 50, 0), "1");

        var result = await service.AddRowAsync(mapId, new MapRowRequest(source, 6, 0, RowDirection.Right));

        Assert.That(result.Shapes.Select(sh => sh.X), Is.All.LessThanOrEqualTo(1500),
            "Every stall stays on the map, even where the row asked for more room than there is.");
    }

    [Test]
    public async Task An_underlay_is_taken_from_its_own_bytes_and_anything_else_is_refused()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");

        var html = await service.SetBackgroundAsync(mapId, "<html><script>alert(1)</script>"u8.ToArray());
        var png = await service.SetBackgroundAsync(mapId, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2]);

        var stored = await service.GetBackgroundAsync(mapId);
        Assert.Multiple(() =>
        {
            Assert.That(html.Errors, Does.Contain("Map_Error_BackgroundNotAnImage"),
                "Whatever is stored here is served back from this origin; a document would be stored XSS.");
            Assert.That(png.Succeeded, Is.True);
            Assert.That(stored!.ContentType, Is.EqualTo(ImageContentType.Png));
        });
    }

    [Test]
    public async Task A_label_too_long_to_be_a_spot_code_is_reported_rather_than_truncated()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var tooLong = new string('X', 40);
        var a = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), tooLong);
        var b = await AddShapeAsync(service, mapId, new MapRect(30, 0, 20, 10, 0), "428");

        var result = await service.CreateSpotsFromShapesAsync(mapId, [a, b], ParkingSpotType.Standard);

        Assert.Multiple(() =>
        {
            Assert.That(result.CreatedCount, Is.EqualTo(1), "The usable one is still created.");
            Assert.That(result.TooLongLabels, Is.EqualTo(new[] { tooLong }),
                "Silently renaming somebody's stall to a prefix of itself is worse than refusing it.");
        });
    }

    [Test]
    public async Task Restoring_deleted_shapes_brings_back_their_geometry_labels_and_links()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var shape = await AddShapeAsync(service, mapId, new MapRect(10, 20, 40, 30, 45), "428");
        var spotId = await CreateSpotAsync("428");
        await service.LinkSpotAsync(shape, spotId);

        await service.DeleteShapesAsync(mapId, [shape]);
        var restored = await service.RestoreShapesAsync(mapId,
            [new MapShapeRestore(MapShapeKind.Spot, new MapRect(10, 20, 40, 30, 45), "428", spotId)]);

        var back = (await service.GetAsync(mapId))!.Shapes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(restored.Succeeded, Is.True);
            Assert.That(back.Label, Is.EqualTo("428"));
            Assert.That((back.X, back.Y, back.Rotation), Is.EqualTo((10d, 20d, 45d)));
            Assert.That(back.ParkingSpotId, Is.EqualTo(spotId));
        });
    }

    [Test]
    public async Task Restoring_never_steals_a_spot_that_was_drawn_in_the_meantime()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");
        var spotId = await CreateSpotAsync("428");
        var original = await AddShapeAsync(service, mapId, new MapRect(0, 0, 20, 10, 0), "428");
        await service.LinkSpotAsync(original, spotId);
        await service.DeleteShapesAsync(mapId, [original]);

        // Somebody redraws 428 before the undo lands.
        var replacement = await AddShapeAsync(service, mapId, new MapRect(60, 0, 20, 10, 0), "428");
        await service.LinkSpotAsync(replacement, spotId);

        var restored = await service.RestoreShapesAsync(mapId,
            [new MapShapeRestore(MapShapeKind.Spot, new MapRect(0, 0, 20, 10, 0), "428", spotId)]);

        var map = await service.GetAsync(mapId);
        Assert.Multiple(() =>
        {
            Assert.That(restored.Succeeded, Is.True, "The rectangle comes back either way.");
            Assert.That(map!.Shapes.Single(sh => sh.Id == replacement).ParkingSpotId, Is.EqualTo(spotId));
            Assert.That(restored.Shapes.Single().ParkingSpotId, Is.Null, "It just comes back unlinked.");
        });
    }

    [Test]
    public async Task Restoring_a_shape_whose_spot_is_gone_brings_the_rectangle_back_unlinked()
    {
        var service = CreateService();
        var mapId = await CreateMapAsync(service, "Areál");

        var restored = await service.RestoreShapesAsync(mapId,
            [new MapShapeRestore(MapShapeKind.Spot, new MapRect(0, 0, 20, 10, 0), "428", Guid.NewGuid())]);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Succeeded, Is.True);
            Assert.That(restored.Shapes.Single().ParkingSpotId, Is.Null);
        });
    }

    [Test]
    public async Task Import_refuses_a_payload_with_more_shapes_than_any_plan_has()
    {
        var service = CreateService();
        var shapes = string.Join(",", Enumerable.Repeat(
            """{"Kind":"Spot","Label":"1","X":0,"Y":0,"Width":10,"Height":10,"Rotation":0}""", 10_001));
        var json = $$"""{"Version":1,"Name":"X","Width":1600,"Height":900,"GridSize":5,"Shapes":[{{shapes}}]}""";

        var result = await service.ImportAsync("Kopie", json);

        Assert.That(result.Errors, Does.Contain("Map_Error_ImportTooLarge"));
    }

    private LotMapService CreateService() => new(new TestDbContextFactory(_options), new FixedTimeProvider(Now));

    private static async Task<Guid> CreateMapAsync(LotMapService service, string name)
    {
        var result = await service.CreateAsync(name, 1600, 900);
        Assert.That(result.Succeeded, Is.True, $"Creating the map fixture failed: {string.Join(", ", result.Errors)}");
        return (await service.ListAsync()).Single(m => m.Name == name).Id;
    }

    private static async Task<Guid> AddShapeAsync(
        LotMapService service, Guid mapId, MapRect rect, string? label, MapShapeKind kind = MapShapeKind.Spot)
    {
        var result = await service.AddShapeAsync(mapId, kind, rect, label);
        Assert.That(result.Succeeded, Is.True, $"Adding the shape fixture failed: {string.Join(", ", result.Errors)}");
        return result.Shapes.Single().Id;
    }

    private async Task<Guid> CreateSpotAsync(string code)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        var spot = new ParkingSpot(code, ParkingSpotType.Standard);
        dbContext.ParkingSpots.Add(spot);
        await dbContext.SaveChangesAsync();
        return spot.Id;
    }
}
