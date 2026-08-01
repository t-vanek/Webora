using D3Parking.Application.Parking.Maps;
using D3Parking.Domain.Parking.Maps;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// The map engine's pure core: the rectangle-with-an-angle every shape is made of, the label
/// sequence that carries numbering along a row, and the row expansion built on both. No I/O, so
/// these pin the arithmetic the editor and the service both depend on.
/// </summary>
[TestFixture]
public class MapGeometryTests
{
    private const double Tolerance = 0.001;

    [Test]
    public void An_upright_rectangle_has_its_four_corners_in_drawing_order()
    {
        var corners = new MapRect(10, 20, 40, 30, 0).Corners();

        Assert.That(corners, Is.EqualTo(new[] { (10d, 20d), (50d, 20d), (50d, 50d), (10d, 50d) }));
    }

    [Test]
    public void A_quarter_turn_maps_each_corner_onto_the_next()
    {
        // A square rotated 90° about its own centre lands on itself, corner for corner.
        var corners = new MapRect(0, 0, 10, 10, 90).Corners();

        Assert.Multiple(() =>
        {
            Assert.That(corners[0].X, Is.EqualTo(10).Within(Tolerance));
            Assert.That(corners[0].Y, Is.EqualTo(0).Within(Tolerance));
            Assert.That(corners[2].X, Is.EqualTo(0).Within(Tolerance));
            Assert.That(corners[2].Y, Is.EqualTo(10).Within(Tolerance));
        });
    }

    [Test]
    public void Rotation_keeps_the_centre_where_it_was()
    {
        var rect = new MapRect(100, 50, 40, 20, 37);
        var corners = rect.Corners();

        Assert.Multiple(() =>
        {
            Assert.That(corners.Average(c => c.X), Is.EqualTo(rect.CentreX).Within(Tolerance));
            Assert.That(corners.Average(c => c.Y), Is.EqualTo(rect.CentreY).Within(Tolerance));
        });
    }

    [Test]
    public void The_bounding_box_of_a_rotated_shape_grows_past_its_own_extents()
    {
        var (minX, minY, maxX, maxY) = new MapRect(0, 0, 20, 10, 45).Bounds();

        Assert.Multiple(() =>
        {
            // A 20×10 box turned 45° spans (20+10)/√2 ≈ 21.21 each way.
            Assert.That(maxX - minX, Is.EqualTo(21.213).Within(0.01));
            Assert.That(maxY - minY, Is.EqualTo(21.213).Within(0.01));
        });
    }

    [Test]
    public void A_drag_that_went_up_and_left_normalizes_into_a_positive_box()
    {
        var normalized = new MapRect(100, 100, -40, -30, 0).Normalized();

        Assert.That(normalized, Is.EqualTo(new MapRect(60, 70, 40, 30, 0)));
    }

    [Test]
    public void Sanitizing_folds_the_angle_into_one_turn_and_rounds_to_storage_precision()
    {
        var sane = new MapRect(1.23456, 2.99999, 10.5, 20.5, -90).Sanitized();

        Assert.Multiple(() =>
        {
            Assert.That(sane.X, Is.EqualTo(1.23));
            Assert.That(sane.Y, Is.EqualTo(3));
            Assert.That(sane.Rotation, Is.EqualTo(270), "A negative angle is the same heading as its positive twin.");
        });
    }

    [TestCase(0, 0, Description = "no extent at all")]
    [TestCase(0.5, 10, Description = "thinner than the minimum")]
    [TestCase(200_000, 10, Description = "past the coordinate guard rail")]
    public void A_rectangle_without_real_extents_is_refused(double width, double height)
    {
        Assert.That(new MapRect(0, 0, width, height, 0).IsValid(), Is.False);
    }

    [Test]
    public void Geometry_that_is_not_a_number_is_refused_rather_than_stored()
    {
        // These arrive from a pointer drag in a browser; a NaN must never reach the database.
        Assert.That(new MapRect(double.NaN, 0, 10, 10, 0).IsValid(), Is.False);
        Assert.That(new MapRect(0, 0, 10, 10, double.PositiveInfinity).IsValid(), Is.False);
    }

    [Test]
    public void A_local_offset_on_an_upright_shape_is_the_offset_itself()
    {
        Assert.That(new MapRect(0, 0, 10, 10, 0).LocalOffset(5, 3), Is.EqualTo((5d, 3d)));
    }

    [Test]
    public void A_local_offset_turns_with_the_shape()
    {
        // Stepping "right" on a shape turned 90° is stepping down the map.
        var (x, y) = new MapRect(0, 0, 10, 10, 90).LocalOffset(10, 0);

        Assert.Multiple(() =>
        {
            Assert.That(x, Is.EqualTo(0).Within(Tolerance));
            Assert.That(y, Is.EqualTo(10).Within(Tolerance));
        });
    }

    [Test]
    public void Svg_points_are_written_invariantly_whatever_the_thread_culture()
    {
        using var _ = new CultureScope("cs-CZ");

        var points = new MapRect(1.5, 2.5, 10, 10, 0).ToSvgPoints();

        Assert.That(points, Does.StartWith("1.5,2.5"),
            "A Czech decimal comma in a points attribute is a broken polygon, not a rounding detail.");
    }

    // --- label sequence ---

    [TestCase("428", "429")]
    [TestCase("H1", "H2")]
    [TestCase("A-01", "A-02")]
    [TestCase("A-09", "A-10")]
    [TestCase("P2-099", "P2-100")]
    public void A_numbered_label_advances_and_keeps_its_padding(string label, string expected)
    {
        Assert.That(MapLabelSequence.Next(label), Is.EqualTo(expected));
    }

    [TestCase("VISITOR")]
    [TestCase("")]
    [TestCase(null)]
    public void A_label_with_no_trailing_number_has_no_successor(string? label)
    {
        Assert.That(MapLabelSequence.Next(label), Is.Null);
    }

    [Test]
    public void The_step_may_run_the_numbering_backwards()
    {
        Assert.That(MapLabelSequence.Next("440", -1), Is.EqualTo("439"));
    }

    // --- row expansion ---

    [Test]
    public void A_row_marches_edge_to_edge_with_the_requested_gap()
    {
        var source = new MapRect(0, 0, 20, 10, 0);

        var row = MapRowPlan.Expand(source, "428", count: 3, gap: 2, RowDirection.Right);

        Assert.Multiple(() =>
        {
            Assert.That(row.Succeeded, Is.True);
            Assert.That(row.Shapes.Select(s => s.Rect.X), Is.EqualTo(new[] { 0d, 22d, 44d }));
            Assert.That(row.Shapes.Select(s => s.Label), Is.EqualTo(new[] { "428", "429", "430" }));
        });
    }

    [Test]
    public void The_source_shape_is_the_first_entry_so_the_caller_can_skip_what_already_exists()
    {
        var source = new MapRect(5, 7, 20, 10, 0);

        var row = MapRowPlan.Expand(source, "1", count: 4, gap: 0, RowDirection.Right);

        Assert.That(row.Shapes[0].Rect, Is.EqualTo(source.Sanitized()));
    }

    [Test]
    public void A_row_of_a_rotated_stall_steps_along_its_own_axis()
    {
        // The whole point of stepping in the shape's frame: a stall drawn at 90° repeats downwards,
        // which is what the fanned-out rows on a real site plan need.
        var source = new MapRect(0, 0, 20, 10, 90);

        var row = MapRowPlan.Expand(source, "1", count: 2, gap: 0, RowDirection.Right);

        Assert.Multiple(() =>
        {
            Assert.That(row.Shapes[1].Rect.X, Is.EqualTo(0).Within(Tolerance));
            Assert.That(row.Shapes[1].Rect.Y, Is.EqualTo(20).Within(Tolerance));
            Assert.That(row.Shapes[1].Rect.Rotation, Is.EqualTo(90), "Repeating must not straighten the copies.");
        });
    }

    [Test]
    public void Down_steps_across_the_height_rather_than_the_width()
    {
        var row = MapRowPlan.Expand(new MapRect(0, 0, 20, 10, 0), null, count: 2, gap: 5, RowDirection.Down);

        Assert.That(row.Shapes[1].Rect.Y, Is.EqualTo(15));
    }

    [Test]
    public void Left_and_up_run_the_row_backwards()
    {
        var left = MapRowPlan.Expand(new MapRect(100, 100, 20, 10, 0), null, 2, 0, RowDirection.Left);
        var up = MapRowPlan.Expand(new MapRect(100, 100, 20, 10, 0), null, 2, 0, RowDirection.Up);

        Assert.Multiple(() =>
        {
            Assert.That(left.Shapes[1].Rect.X, Is.EqualTo(80));
            Assert.That(up.Shapes[1].Rect.Y, Is.EqualTo(90));
        });
    }

    [Test]
    public void A_source_without_a_numbered_label_still_draws_the_row_unnamed()
    {
        var row = MapRowPlan.Expand(new MapRect(0, 0, 20, 10, 0), "VISITOR", count: 3, gap: 0, RowDirection.Right);

        Assert.Multiple(() =>
        {
            Assert.That(row.Succeeded, Is.True);
            Assert.That(row.Shapes.Select(s => s.Label), Is.EqualTo(new[] { "VISITOR", null, null }));
        });
    }

    [TestCase(0)]
    [TestCase(MapRowPlan.MaxRowLength + 1)]
    public void A_row_length_outside_the_typo_guard_is_refused(int count)
    {
        var row = MapRowPlan.Expand(new MapRect(0, 0, 20, 10, 0), "1", count, 0, RowDirection.Right);

        Assert.Multiple(() =>
        {
            Assert.That(row.Succeeded, Is.False);
            Assert.That(row.ErrorKey, Is.EqualTo("Map_Error_RowLength"));
        });
    }

    [Test]
    public void An_unusable_source_rectangle_is_refused_before_anything_is_planned()
    {
        var row = MapRowPlan.Expand(new MapRect(0, 0, 0, 0, 0), "1", 5, 0, RowDirection.Right);

        Assert.That(row.ErrorKey, Is.EqualTo("Map_Error_ShapeGeometry"));
    }

    [Test]
    public void A_row_that_walks_off_the_coordinate_space_returns_the_part_that_fits()
    {
        // A huge gap runs past MaxCoordinate part-way through; the stalls that still land are a more
        // useful answer than refusing the whole row.
        var row = MapRowPlan.Expand(new MapRect(0, 0, 20, 10, 0), "1", count: 10, gap: 40_000, RowDirection.Right);

        Assert.Multiple(() =>
        {
            Assert.That(row.Succeeded, Is.True);
            Assert.That(row.Shapes, Has.Count.LessThan(10));
            Assert.That(row.Shapes, Is.Not.Empty);
        });
    }

    // --- clamping into the map ---

    [Test]
    public void A_shape_dragged_past_the_edge_is_slid_back_onto_the_map()
    {
        var clamped = new MapRect(1580, 890, 40, 30, 0).ClampedInto(1600, 900);

        Assert.That(clamped, Is.EqualTo(new MapRect(1560, 870, 40, 30, 0)),
            "Off the canvas is unreachable: it cannot be clicked and the zoom is bounded, so it is lost work.");
    }

    [Test]
    public void A_shape_dragged_off_the_top_left_is_pulled_back_too()
    {
        Assert.That(new MapRect(-30, -20, 40, 30, 0).ClampedInto(1600, 900), Is.EqualTo(new MapRect(0, 0, 40, 30, 0)));
    }

    [Test]
    public void A_shape_already_inside_the_map_is_left_exactly_as_it_was()
    {
        var rect = new MapRect(100, 100, 40, 30, 17);

        Assert.That(rect.ClampedInto(1600, 900), Is.EqualTo(rect));
    }

    [Test]
    public void Clamping_measures_the_rotated_bounding_box_so_a_turned_shape_stays_whole()
    {
        // A 20×10 box at 45° spans ~21.2 each way, so its corner pokes out well before its own x does.
        var clamped = new MapRect(0, 0, 20, 10, 45).ClampedInto(1600, 900);
        var (minX, minY, _, _) = clamped.Bounds();

        Assert.Multiple(() =>
        {
            Assert.That(minX, Is.EqualTo(0).Within(Tolerance));
            Assert.That(minY, Is.EqualTo(0).Within(Tolerance));
        });
    }

    [Test]
    public void A_shape_larger_than_the_map_pins_to_the_corner_rather_than_being_squashed()
    {
        var clamped = new MapRect(-50, -50, 400, 300, 0).ClampedInto(200, 150);

        Assert.Multiple(() =>
        {
            Assert.That((clamped.X, clamped.Y), Is.EqualTo((0d, 0d)));
            Assert.That((clamped.Width, clamped.Height), Is.EqualTo((400d, 300d)),
                "Clamping moves a shape; it never resizes one behind the author's back.");
        });
    }

    // --- what an uploaded underlay is allowed to be ---

    [Test]
    public void The_raster_formats_are_recognised_by_their_own_bytes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ImageContentType.Detect([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]),
                Is.EqualTo(ImageContentType.Png));
            Assert.That(ImageContentType.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]), Is.EqualTo(ImageContentType.Jpeg));
            Assert.That(ImageContentType.Detect("RIFF\0\0\0\0WEBPVP8 "u8), Is.EqualTo(ImageContentType.Webp));
        });
    }

    [Test]
    public void An_upload_that_is_not_one_of_those_formats_is_refused()
    {
        Assert.Multiple(() =>
        {
            // The one that matters: HTML stored as an "image" and served back from this origin is
            // stored cross-site scripting, whatever the upload claimed its content type was.
            Assert.That(ImageContentType.Detect("<html><script>alert(1)</script>"u8), Is.Null);
            // SVG is an image and would trace beautifully — and carries script, so it stays out.
            Assert.That(ImageContentType.Detect("<svg xmlns=\"http://www.w3.org/2000/svg\">"u8), Is.Null);
            Assert.That(ImageContentType.Detect([]), Is.Null);
            Assert.That(ImageContentType.Detect([0x89, (byte)'P']), Is.Null);
        });
    }

    /// <summary>Pins the thread culture for one test, so the invariant-formatting assertion means something.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) =>
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
