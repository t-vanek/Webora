using D3Parking.Application.Parking.Maps;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// The importer that turns an exported site plan into stalls. Pure, so every shape an exporter might
/// write can be put through it exactly — including the ones it must refuse, because an import that
/// quietly loses forty stalls is worse than one that says it could not read them.
/// </summary>
[TestFixture]
public class SvgPlanReaderTests
{
    private const double Tolerance = 0.01;

    private static string Svg(string body, string attributes = @"viewBox=""0 0 1000 500""") =>
        $@"<svg xmlns=""http://www.w3.org/2000/svg"" {attributes}>{body}</svg>";

    // --- the canvas ---

    [Test]
    public void The_view_box_is_the_drawing_size_and_its_origin_is_moved_to_zero()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<rect x=""110"" y=""60"" width=""20"" height=""10"" />",
            @"viewBox=""100 50 800 400"" width=""1600"" height=""800"""));

        Assert.Multiple(() =>
        {
            Assert.That((reading.Width, reading.Height), Is.EqualTo((800d, 400d)),
                "The viewBox is the coordinate system the contents are drawn in; width and height are only how big it is printed.");
            var rect = reading.Shapes.Single().Rect;
            Assert.That((rect.X, rect.Y), Is.EqualTo((10d, 10d)), "A viewBox that starts away from the origin shifts everything with it.");
        });
    }

    [Test]
    public void Width_and_height_stand_in_when_there_is_no_view_box()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<rect x=""0"" y=""0"" width=""20"" height=""10"" />", @"width=""640"" height=""480"""));

        Assert.That((reading.Width, reading.Height), Is.EqualTo((640d, 480d)));
    }

    [TestCase("", "Map_Error_SvgEmpty")]
    [TestCase("<svg><rect", "Map_Error_SvgUnreadable")]
    [TestCase("<html><body/></html>", "Map_Error_SvgNotAPlan")]
    public void A_file_that_is_not_a_readable_plan_is_refused_by_name(string content, string expected)
    {
        var reading = SvgPlanReader.Read(content);

        Assert.Multiple(() =>
        {
            Assert.That(reading.Succeeded, Is.False);
            Assert.That(reading.ErrorKey, Is.EqualTo(expected));
        });
    }

    [Test]
    public void A_drawing_with_no_size_at_all_is_refused()
    {
        var reading = SvgPlanReader.Read(@"<svg xmlns=""http://www.w3.org/2000/svg""><rect width=""5"" height=""5""/></svg>");

        Assert.That(reading.ErrorKey, Is.EqualTo("Map_Error_SvgNoSize"));
    }

    // --- the four ways a stall gets written ---

    [Test]
    public void A_rect_becomes_a_stall()
    {
        var reading = SvgPlanReader.Read(Svg(@"<rect x=""10"" y=""20"" width=""40"" height=""30"" />"));

        Assert.That(reading.Shapes.Single().Rect, Is.EqualTo(new MapRectLike(10, 20, 40, 30, 0)).Using(RectComparer));
    }

    [Test]
    public void A_polygon_becomes_a_stall()
    {
        var reading = SvgPlanReader.Read(Svg(@"<polygon points=""10,20 50,20 50,50 10,50"" />"));

        Assert.That(reading.Shapes.Single().Rect, Is.EqualTo(new MapRectLike(10, 20, 40, 30, 0)).Using(RectComparer));
    }

    [Test]
    public void A_path_of_straight_lines_becomes_a_stall()
    {
        // What a PDF converted to SVG produces for every box in the file — a reader that only knows
        // <rect> finds nothing at all in the format it is most likely to be handed.
        var reading = SvgPlanReader.Read(Svg(@"<path d=""M 10 20 L 50 20 L 50 50 L 10 50 Z"" />"));

        Assert.That(reading.Shapes.Single().Rect, Is.EqualTo(new MapRectLike(10, 20, 40, 30, 0)).Using(RectComparer));
    }

    [Test]
    public void A_path_written_with_relative_and_shorthand_commands_reads_the_same()
    {
        var reading = SvgPlanReader.Read(Svg(@"<path d=""m 10 20 h 40 v 30 h -40 z"" />"));

        Assert.That(reading.Shapes.Single().Rect, Is.EqualTo(new MapRectLike(10, 20, 40, 30, 0)).Using(RectComparer));
    }

    [Test]
    public void A_path_that_repeats_its_first_point_to_close_is_still_four_corners()
    {
        var reading = SvgPlanReader.Read(Svg(@"<path d=""M 10 20 L 50 20 L 50 50 L 10 50 L 10 20 Z"" />"));

        Assert.That(reading.Shapes, Has.Count.EqualTo(1));
    }

    [Test]
    public void Anything_curved_is_counted_as_not_a_rectangle_rather_than_guessed_at()
    {
        var reading = SvgPlanReader.Read(Svg(@"<path d=""M 10 20 C 30 20 50 40 50 50 Z"" />"));

        Assert.Multiple(() =>
        {
            Assert.That(reading.Shapes, Is.Empty);
            Assert.That(reading.Warnings.NotRectangles + reading.Warnings.Degenerate, Is.EqualTo(0),
                "A curve is not a shape the reader ever built a rectangle from, so it is not a rectangle it rejected.");
        });
    }

    [Test]
    public void A_four_cornered_shape_that_is_not_square_is_refused_and_counted()
    {
        // A kerb or a verge: four corners, no right angles.
        var reading = SvgPlanReader.Read(Svg(@"<polygon points=""0,0 40,5 45,35 5,30"" />"));

        Assert.Multiple(() =>
        {
            Assert.That(reading.Shapes, Is.Empty);
            Assert.That(reading.Warnings.NotRectangles, Is.EqualTo(1));
        });
    }

    [Test]
    public void A_hairline_with_no_area_is_counted_as_degenerate()
    {
        var reading = SvgPlanReader.Read(Svg(@"<rect x=""0"" y=""0"" width=""40"" height=""0"" />"));

        Assert.Multiple(() =>
        {
            Assert.That(reading.Shapes, Is.Empty);
            Assert.That(reading.Warnings.Degenerate, Is.EqualTo(1));
        });
    }

    // --- transforms, which decide where a stall actually is ---

    [Test]
    public void A_group_transform_moves_the_stalls_inside_it()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<g transform=""translate(100 50)""><rect x=""10"" y=""20"" width=""40"" height=""30"" /></g>"));

        Assert.That(reading.Shapes.Single().Rect, Is.EqualTo(new MapRectLike(110, 70, 40, 30, 0)).Using(RectComparer));
    }

    [Test]
    public void Nested_group_transforms_are_composed_not_replaced()
    {
        // The case that puts every stall in the wrong place if the groups above it are skipped.
        var reading = SvgPlanReader.Read(Svg(
            @"<g transform=""translate(100 0)""><g transform=""translate(0 50) scale(2)"">
                 <rect x=""5"" y=""10"" width=""20"" height=""15"" /></g></g>"));

        Assert.That(reading.Shapes.Single().Rect, Is.EqualTo(new MapRectLike(110, 70, 40, 30, 0)).Using(RectComparer));
    }

    [Test]
    public void A_rotated_group_yields_a_rotated_stall_rather_than_a_rejected_one()
    {
        // The fanned-out rows of a real plan arrive exactly like this.
        var reading = SvgPlanReader.Read(Svg(
            @"<g transform=""rotate(90 100 100)""><rect x=""90"" y=""90"" width=""40"" height=""20"" /></g>"));

        var rect = reading.Shapes.Single().Rect;
        Assert.Multiple(() =>
        {
            Assert.That(rect.Rotation, Is.EqualTo(90).Within(Tolerance));
            Assert.That((rect.Width, rect.Height), Is.EqualTo((40d, 20d)), "Turning a stall does not resize it.");
        });
    }

    [Test]
    public void A_matrix_transform_is_read_like_any_other()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<g transform=""matrix(1 0 0 1 10 20)""><rect x=""0"" y=""0"" width=""40"" height=""30"" /></g>"));

        Assert.That(reading.Shapes.Single().Rect, Is.EqualTo(new MapRectLike(10, 20, 40, 30, 0)).Using(RectComparer));
    }

    [Test]
    public void A_transform_the_reader_cannot_apply_is_reported_rather_than_ignored()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<g transform=""skewX(20)""><rect x=""0"" y=""0"" width=""40"" height=""30"" /></g>"));

        Assert.That(reading.Warnings.UnsupportedTransforms, Is.EqualTo(1),
            "A skew turns a rectangle into something else; pretending it was identity would place the stall wrongly and say nothing.");
    }

    [Test]
    public void Definitions_and_clip_paths_are_not_part_of_the_drawing()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<defs><rect x=""0"" y=""0"" width=""40"" height=""30"" /></defs>
              <clipPath id=""c""><rect x=""0"" y=""0"" width=""40"" height=""30"" /></clipPath>
              <rect x=""10"" y=""20"" width=""40"" height=""30"" />"));

        Assert.That(reading.Shapes, Has.Count.EqualTo(1));
    }

    // --- labels, which is what makes the import worth having ---

    [Test]
    public void Text_printed_inside_a_stall_names_it()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<rect x=""10"" y=""20"" width=""40"" height=""30"" /><text x=""30"" y=""35"">428</text>"));

        Assert.That(reading.Shapes.Single().Label, Is.EqualTo("428"));
    }

    [Test]
    public void Each_stall_gets_the_number_printed_in_it_and_not_its_neighbours()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<rect x=""0"" y=""0"" width=""40"" height=""30"" /><rect x=""50"" y=""0"" width=""40"" height=""30"" />
              <text x=""70"" y=""15"">429</text><text x=""20"" y=""15"">428</text>"));

        Assert.That(reading.Shapes.Select(s => s.Label), Is.EqualTo(new[] { "428", "429" }),
            "Labels follow the geometry they sit in, not the order they appear in the file.");
    }

    [Test]
    public void A_label_in_a_tspan_is_found_too()
    {
        // Inkscape writes text this way; the position is on the span, not on the text element.
        var reading = SvgPlanReader.Read(Svg(
            @"<rect x=""10"" y=""20"" width=""40"" height=""30"" /><text><tspan x=""30"" y=""35"">434</tspan></text>"));

        Assert.That(reading.Shapes.Single().Label, Is.EqualTo("434"));
    }

    [Test]
    public void A_label_just_outside_its_stall_still_finds_it()
    {
        // Text is anchored on a baseline, which some exports put a hair below the box it belongs to.
        var reading = SvgPlanReader.Read(Svg(
            @"<rect x=""10"" y=""20"" width=""40"" height=""30"" /><text x=""30"" y=""52"">428</text>"));

        Assert.That(reading.Shapes.Single().Label, Is.EqualTo("428"));
    }

    [Test]
    public void A_caption_far_from_every_stall_is_left_off_and_counted()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<rect x=""10"" y=""20"" width=""40"" height=""30"" />
              <text x=""800"" y=""400"">ADMINISTRATIVNÍ BUDOVA č.2</text>"));

        Assert.Multiple(() =>
        {
            Assert.That(reading.Shapes.Single().Label, Is.Null,
                "A building's name is nearest to some stall too; naming that stall after the building would be worse than leaving it unnamed.");
            Assert.That(reading.Warnings.OrphanLabels, Is.EqualTo(1));
        });
    }

    [Test]
    public void A_second_label_for_the_same_stall_does_not_overwrite_the_first()
    {
        var reading = SvgPlanReader.Read(Svg(
            @"<rect x=""10"" y=""20"" width=""40"" height=""30"" />
              <text x=""25"" y=""35"">428</text><text x=""35"" y=""35"">VISITOR</text>"));

        Assert.Multiple(() =>
        {
            Assert.That(reading.Shapes.Single().Label, Is.EqualTo("428"));
            Assert.That(reading.Warnings.OrphanLabels, Is.EqualTo(1));
        });
    }

    [Test]
    public void A_realistic_row_reads_as_a_row_of_numbered_stalls()
    {
        // Eight stalls edge to edge with their numbers in them, drawn inside a positioned group —
        // the shape of every row on the plan this was built for.
        var stalls = string.Concat(Enumerable.Range(0, 8).Select(i =>
            $@"<path d=""M {i * 50} 0 L {(i * 50) + 40} 0 L {(i * 50) + 40} 30 L {i * 50} 30 Z"" />" +
            $@"<text x=""{(i * 50) + 20}"" y=""18"">{428 + i}</text>"));

        var reading = SvgPlanReader.Read(Svg($@"<g transform=""translate(100 200)"">{stalls}</g>"));

        Assert.Multiple(() =>
        {
            Assert.That(reading.Shapes, Has.Count.EqualTo(8));
            Assert.That(reading.Shapes.Select(s => s.Label),
                Is.EqualTo(new[] { "428", "429", "430", "431", "432", "433", "434", "435" }));
            Assert.That(reading.Shapes[0].Rect.X, Is.EqualTo(100).Within(Tolerance));
            Assert.That(reading.Shapes[0].Rect.Y, Is.EqualTo(200).Within(Tolerance));
            Assert.That(reading.Warnings.OrphanLabels, Is.EqualTo(0));
        });
    }

    /// <summary>Compares the numbers of a rectangle within tolerance, since transforms bring rounding.</summary>
    private static readonly Comparison<object> RectComparer = (a, b) =>
    {
        var (x, y) = (Rect(a), Rect(b));
        return Math.Abs(x.X - y.X) < Tolerance && Math.Abs(x.Y - y.Y) < Tolerance
            && Math.Abs(x.W - y.W) < Tolerance && Math.Abs(x.H - y.H) < Tolerance
            && Math.Abs(x.R - y.R) < Tolerance
            ? 0
            : 1;
    };

    private static (double X, double Y, double W, double H, double R) Rect(object value) => value switch
    {
        MapRectLike like => (like.X, like.Y, like.W, like.H, like.R),
        D3Parking.Domain.Parking.Maps.MapRect rect => (rect.X, rect.Y, rect.Width, rect.Height, rect.Rotation),
        _ => throw new ArgumentException($"Not a rectangle: {value}", nameof(value)),
    };

    /// <summary>The expected numbers, spelled out so a failure reads as five values rather than a type name.</summary>
    private sealed record MapRectLike(double X, double Y, double W, double H, double R);
}
