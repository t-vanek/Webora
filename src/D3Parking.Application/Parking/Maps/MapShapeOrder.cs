using D3Parking.Domain.Parking.Maps;

namespace D3Parking.Application.Parking.Maps;

/// <summary>
/// Puts a selection of shapes into the order a person reads them: bands from the top down, and
/// within a band from the left. Pure, so renumbering can be previewed with the same call that
/// performs it.
/// </summary>
/// <remarks>
/// Reading order rather than "sort by x, then y", because a selection is one of three things and all
/// three have to come out right: a horizontal row (one band, ordered left to right), a vertical
/// column (every stall its own band, so it falls out top to bottom), and a block (bands top down,
/// each read left to right). Banding by half the typical shape height is what distinguishes them
/// without asking the author which one they drew.
/// </remarks>
public static class MapShapeOrder
{
    public static IReadOnlyList<T> Reading<T>(IReadOnlyList<T> shapes, Func<T, MapRect> geometry, bool reverse = false)
    {
        if (shapes.Count <= 1)
        {
            return shapes;
        }

        var placed = shapes
            .Select(shape =>
            {
                var (minX, minY, maxX, maxY) = geometry(shape).Bounds();
                return new
                {
                    Shape = shape,
                    CentreX = (minX + maxX) / 2,
                    CentreY = (minY + maxY) / 2,
                    Height = maxY - minY,
                };
            })
            .OrderBy(p => p.CentreY)
            .ToList();

        // Half the median height: tall enough that a row of stalls drawn a few units apart stays one
        // band, short enough that a column of them never collapses into one.
        var heights = placed.Select(p => p.Height).OrderBy(h => h).ToList();
        var tolerance = Math.Max(heights[heights.Count / 2] / 2, 0.5);

        var ordered = new List<T>(shapes.Count);
        var band = new List<(T Shape, double CentreX)>();
        var bandTop = placed[0].CentreY;

        void FlushBand()
        {
            ordered.AddRange(band.OrderBy(b => b.CentreX).Select(b => b.Shape));
            band.Clear();
        }

        foreach (var item in placed)
        {
            if (item.CentreY - bandTop > tolerance)
            {
                FlushBand();
                bandTop = item.CentreY;
            }

            band.Add((item.Shape, item.CentreX));
        }

        FlushBand();

        if (reverse)
        {
            ordered.Reverse();
        }

        return ordered;
    }
}
