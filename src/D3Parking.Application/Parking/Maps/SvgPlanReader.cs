using System.Xml.Linq;
using D3Parking.Domain.Parking.Maps;

namespace D3Parking.Application.Parking.Maps;

/// <summary>What an import could not make sense of, so the count is stated rather than the loss hidden.</summary>
public sealed record SvgPlanWarnings(
    /// <summary>Drawn shapes that are not rectangles — kerbs, arrows, circles, anything curved.</summary>
    int NotRectangles,
    /// <summary>Rectangles with no area. Hairlines and artefacts of the export.</summary>
    int Degenerate,
    /// <summary>Transforms the reader does not implement (the skews, which un-rectangle a rectangle).</summary>
    int UnsupportedTransforms,
    /// <summary>Text that sits inside no rectangle and near none — building names, captions, legends.</summary>
    int OrphanLabels,
    /// <summary>Rectangles dropped for standing exactly on one already read — see the reader's remarks.</summary>
    int Duplicates);

/// <summary>The geometry read out of an exported plan, in the drawing's own coordinates.</summary>
public sealed record SvgPlanReading(
    double Width,
    double Height,
    IReadOnlyList<PlannedShape> Shapes,
    SvgPlanWarnings Warnings,
    string? ErrorKey)
{
    public bool Succeeded => ErrorKey is null;

    public static SvgPlanReading Failure(string errorKey) =>
        new(0, 0, [], new SvgPlanWarnings(0, 0, 0, 0, 0), errorKey);
}

/// <summary>
/// Reads the stalls out of a site plan exported to SVG, so a plan of hundreds of them is a file to
/// open rather than an afternoon of tracing. Pure: a string in, rectangles and labels out.
/// </summary>
/// <remarks>
/// This is not a general SVG renderer and does not pretend to be. It looks for the one thing a
/// parking plan is made of — closed four-cornered shapes — in the four ways exporters write them
/// (<c>rect</c>, <c>polygon</c>, <c>polyline</c>, and a <c>path</c> of straight lines, which is what
/// a PDF converted to SVG produces for everything, often packing a whole row of stalls into one path
/// as separate subpaths). Group transforms are composed on the way down, because a stall's real
/// position is the product of every group above it. Everything it cannot make a rectangle of is
/// counted and reported, never quietly dropped: an import that silently loses forty stalls is worse
/// than one that refuses, and a counter that stays at zero while shapes vanish is worse still.
///
/// One thing is dropped on purpose and counted separately: a rectangle standing exactly on one
/// already read. Converters routinely emit the same box twice, once filled and once stroked, and
/// stacking two shapes per stall would double every number on the map. Exactly is meant literally —
/// same centre, size and angle to storage precision — which no two real stalls ever are.
///
/// The numbers come back in the drawing's own coordinates. Fitting them into a map's coordinate space
/// is the caller's decision, not the reader's.
/// </remarks>
public static class SvgPlanReader
{
    /// <summary>Cosine of the angle between adjacent sides. 0.02 is about a degree of slack.</summary>
    private const double SquareTolerance = 0.02;

    /// <summary>How far the fourth corner may miss, as a share of the shape's own diagonal.</summary>
    private const double ClosureTolerance = 0.02;

    /// <summary>Elements whose contents are definitions or clipping, never part of the drawing.</summary>
    private static readonly HashSet<string> Ignored =
        ["defs", "clipPath", "mask", "symbol", "marker", "pattern", "metadata", "title", "desc"];

    /// <summary>
    /// Elements that put ink on the page. One of these that yields no rectangle is a loss and has to
    /// be counted; anything outside this set (a <c>style</c>, a <c>filter</c>, an <c>image</c>) was
    /// never a stall and counting it would drown the real warnings in noise.
    /// </summary>
    private static readonly HashSet<string> Graphical =
        ["rect", "polygon", "polyline", "path", "circle", "ellipse", "line"];

    /// <summary>Upper bound on one file, so a pathological export cannot be walked forever.</summary>
    public const int MaxShapes = 20_000;

    public static SvgPlanReading Read(string svg)
    {
        if (string.IsNullOrWhiteSpace(svg))
        {
            return SvgPlanReading.Failure("Map_Error_SvgEmpty");
        }

        XDocument document;
        try
        {
            // DTD processing stays off (the default): an imported file must not be able to make the
            // parser fetch anything or expand an entity bomb.
            document = XDocument.Parse(svg, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return SvgPlanReading.Failure("Map_Error_SvgUnreadable");
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            return SvgPlanReading.Failure("Map_Error_SvgNotAPlan");
        }

        var (width, height, rootTransform) = ReadCanvas(root);
        if (width <= 0 || height <= 0)
        {
            return SvgPlanReading.Failure("Map_Error_SvgNoSize");
        }

        var rects = new List<MapRect>();
        var seen = new HashSet<MapRect>();
        var labels = new List<(string Text, double X, double Y)>();
        var notRectangles = 0;
        var degenerate = 0;
        var unsupported = 0;
        var duplicates = 0;

        Walk(root, rootTransform);

        void Walk(XElement element, SvgTransform inherited)
        {
            foreach (var child in element.Elements())
            {
                var name = child.Name.LocalName;
                if (Ignored.Contains(name))
                {
                    continue;
                }

                var local = SvgTransform.Parse((string?)child.Attribute("transform"), out var bad);
                if (bad)
                {
                    unsupported++;
                }

                var transform = inherited.Compose(local);

                if (name is "g" or "svg" or "a" or "switch")
                {
                    Walk(child, transform);
                    continue;
                }

                if (name is "text")
                {
                    // The whole text subtree at once: a tspan inherits its parent's position when it
                    // states none, which the generic walk has no way to pass down.
                    ReadText(child, transform, 0, 0);
                    continue;
                }

                if (rects.Count >= MaxShapes)
                {
                    continue;
                }

                var subpaths = Subpaths(child, name);
                if (subpaths is null || subpaths.Count == 0)
                {
                    // A drawn element that gave up nothing readable — a circle, a bezier kerb, a path
                    // this reader cannot follow. Silence here is what made the count lie.
                    if (Graphical.Contains(name))
                    {
                        notRectangles++;
                    }

                    continue;
                }

                foreach (var subpath in subpaths)
                {
                    if (rects.Count >= MaxShapes)
                    {
                        break;
                    }

                    var points = Quad(subpath);
                    if (points is null)
                    {
                        notRectangles++;
                        continue;
                    }

                    // Into the drawing's coordinates before anything is measured: a rectangle inside a
                    // rotated group is only a rotated rectangle once its group's transform is applied.
                    var placed = Array.ConvertAll(points, p => transform.Apply(p.X, p.Y));
                    var rect = RectangleFrom(placed);
                    if (rect is null)
                    {
                        notRectangles++;
                    }
                    else if (rect.Value.Width <= 0 || rect.Value.Height <= 0)
                    {
                        degenerate++;
                    }
                    else if (!seen.Add(rect.Value))
                    {
                        duplicates++;
                    }
                    else
                    {
                        rects.Add(rect.Value);
                    }
                }
            }
        }

        void ReadText(XElement element, SvgTransform transform, double inheritedX, double inheritedY)
        {
            // x and y are optional: absent, the glyphs start at the current text position, which for
            // the outermost element is the origin. Converters that place every run by its own matrix —
            // what a PDF export produces — write no x at all, and refusing those lost every number on
            // the plan while the shapes came through fine.
            var x = SvgNumbers.Single((string?)element.Attribute("x")) ?? inheritedX;
            var y = SvgNumbers.Single((string?)element.Attribute("y")) ?? inheritedY;

            // Only the element's own text, not its descendants': a tspan is read in its own right,
            // and taking both would name the same stall twice.
            var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (text.Length > 0 && labels.Count < MaxShapes)
            {
                var (px, py) = transform.Apply(x, y);
                labels.Add((text, px, py));
            }

            foreach (var child in element.Elements())
            {
                if (Ignored.Contains(child.Name.LocalName))
                {
                    continue;
                }

                var local = SvgTransform.Parse((string?)child.Attribute("transform"), out var bad);
                if (bad)
                {
                    unsupported++;
                }

                ReadText(child, transform.Compose(local), x, y);
            }
        }

        // The candidate outlines an element draws, in its own coordinates — one per subpath, because a
        // converter thinks nothing of putting a whole row of stalls in a single "d". Null when the
        // element draws nothing this reader can follow.
        IReadOnlyList<IReadOnlyList<(double X, double Y)>>? Subpaths(XElement element, string name)
        {
            switch (name)
            {
                case "rect":
                {
                    var x = SvgNumbers.Single((string?)element.Attribute("x")) ?? 0;
                    var y = SvgNumbers.Single((string?)element.Attribute("y")) ?? 0;
                    var w = SvgNumbers.Single((string?)element.Attribute("width")) ?? 0;
                    var h = SvgNumbers.Single((string?)element.Attribute("height")) ?? 0;
                    return [new[] { (x, y), (x + w, y), (x + w, y + h), (x, y + h) }];
                }

                case "polygon":
                case "polyline":
                    return [Pairs(SvgNumbers.Parse((string?)element.Attribute("points")))];

                case "path":
                    return SvgPathPoints.Read((string?)element.Attribute("d"));

                default:
                    return null;
            }
        }

        var (shapes, orphans) = Attach(rects, labels);

        return new SvgPlanReading(
            width,
            height,
            shapes,
            new SvgPlanWarnings(notRectangles, degenerate, unsupported, orphans, duplicates),
            null);
    }

    /// <summary>
    /// The drawing's size and the transform that puts its top-left at the origin. A viewBox wins over
    /// width and height: it is the coordinate system the contents are actually drawn in, and it may
    /// start somewhere other than zero.
    /// </summary>
    private static (double Width, double Height, SvgTransform Root) ReadCanvas(XElement root)
    {
        var box = SvgNumbers.Parse((string?)root.Attribute("viewBox"));
        if (box.Count == 4 && box[2] > 0 && box[3] > 0)
        {
            return (box[2], box[3], new SvgTransform(1, 0, 0, 1, -box[0], -box[1]));
        }

        var width = SvgNumbers.Single((string?)root.Attribute("width")) ?? 0;
        var height = SvgNumbers.Single((string?)root.Attribute("height")) ?? 0;
        return (width, height, SvgTransform.Identity);
    }

    private static IReadOnlyList<(double X, double Y)> Pairs(IReadOnlyList<double> numbers)
    {
        var points = new List<(double, double)>(numbers.Count / 2);
        for (var i = 0; i + 1 < numbers.Count; i += 2)
        {
            points.Add((numbers[i], numbers[i + 1]));
        }

        return points;
    }

    /// <summary>
    /// Four corners, or null. A closed shape often repeats its first point as its last; five points
    /// that close are the same quadrilateral as four.
    /// </summary>
    private static (double X, double Y)[]? Quad(IReadOnlyList<(double X, double Y)>? points)
    {
        if (points is null || points.Count < 4)
        {
            return null;
        }

        if (points.Count == 5 && Close(points[0], points[4]))
        {
            return [points[0], points[1], points[2], points[3]];
        }

        return points.Count == 4 ? [points[0], points[1], points[2], points[3]] : null;
    }

    private static bool Close((double X, double Y) a, (double X, double Y) b) =>
        Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

    /// <summary>
    /// The rectangle four corners describe, or null when they describe something else. This is the
    /// inverse of <see cref="MapRect.Corners"/>: adjacent sides must meet at a right angle and the
    /// fourth corner must land where the first three put it.
    /// </summary>
    private static MapRect? RectangleFrom((double X, double Y)[] p)
    {
        var (ux, uy) = (p[1].X - p[0].X, p[1].Y - p[0].Y);
        var (vx, vy) = (p[3].X - p[0].X, p[3].Y - p[0].Y);
        var width = Math.Sqrt((ux * ux) + (uy * uy));
        var height = Math.Sqrt((vx * vx) + (vy * vy));
        if (width <= 0 || height <= 0)
        {
            return new MapRect(p[0].X, p[0].Y, width, height, 0);
        }

        if (Math.Abs(((ux * vx) + (uy * vy)) / (width * height)) > SquareTolerance)
        {
            return null;
        }

        var diagonal = Math.Sqrt((width * width) + (height * height));
        if (Math.Abs(p[0].X + ux + vx - p[2].X) > diagonal * ClosureTolerance
            || Math.Abs(p[0].Y + uy + vy - p[2].Y) > diagonal * ClosureTolerance)
        {
            return null;
        }

        var rotation = Math.Atan2(uy, ux) * 180d / Math.PI;
        var centreX = (p[0].X + p[2].X) / 2;
        var centreY = (p[0].Y + p[2].Y) / 2;
        return new MapRect(centreX - (width / 2), centreY - (height / 2), width, height, rotation).Sanitized();
    }

    /// <summary>
    /// Gives each rectangle the label printed inside it. A plan puts a stall's number in the middle of
    /// the stall, so containment answers almost every case; the nearest-centre fallback catches the
    /// exports that anchor text on a baseline just outside the box it belongs to.
    /// </summary>
    private static (IReadOnlyList<PlannedShape> Shapes, int Orphans) Attach(
        IReadOnlyList<MapRect> rects,
        IReadOnlyList<(string Text, double X, double Y)> labels)
    {
        var taken = new string?[rects.Count];
        var orphans = 0;

        foreach (var (text, x, y) in labels)
        {
            var index = IndexOfContaining(rects, x, y);
            if (index < 0)
            {
                index = IndexOfNearest(rects, x, y);
            }

            if (index < 0 || taken[index] is not null)
            {
                orphans++;
                continue;
            }

            taken[index] = text;
        }

        return (rects.Select((r, i) => new PlannedShape(r, taken[i])).ToList(), orphans);
    }

    private static int IndexOfContaining(IReadOnlyList<MapRect> rects, double x, double y)
    {
        for (var i = 0; i < rects.Count; i++)
        {
            if (Contains(rects[i], x, y))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Contains(MapRect rect, double x, double y)
    {
        // Into the rectangle's own frame: turn the point back by the rectangle's angle about its
        // centre, and the test is an upright one.
        var radians = -rect.Rotation * Math.PI / 180d;
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
        var (dx, dy) = (x - rect.CentreX, y - rect.CentreY);
        var localX = rect.CentreX + (dx * cos) - (dy * sin);
        var localY = rect.CentreY + (dx * sin) + (dy * cos);
        return localX >= rect.X && localX <= rect.X + rect.Width
            && localY >= rect.Y && localY <= rect.Y + rect.Height;
    }

    /// <summary>
    /// The nearest rectangle, but only if the text is within half its own size of it. Beyond that the
    /// text belongs to nothing — a building's name is nearest to some stall too, and naming that stall
    /// after the building would be worse than leaving it unnamed.
    /// </summary>
    private static int IndexOfNearest(IReadOnlyList<MapRect> rects, double x, double y)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < rects.Count; i++)
        {
            var rect = rects[i];
            var dx = x - rect.CentreX;
            var dy = y - rect.CentreY;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            var reach = Math.Max(rect.Width, rect.Height) * 0.75;
            if (distance < bestDistance && distance <= reach)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }
}
