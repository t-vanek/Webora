using System.Globalization;
using System.Text.RegularExpressions;

namespace D3Parking.Application.Parking.Maps;

/// <summary>
/// A 2D affine transform, in the same six numbers SVG's <c>matrix(a b c d e f)</c> uses:
/// <c>x' = a·x + c·y + e</c>, <c>y' = b·x + d·y + f</c>.
/// </summary>
/// <remarks>
/// An exported site plan nests its groups — a page transform, then a layer, then the drawing — and a
/// rectangle's real position is the product of all of them. Reading the rectangle's own attributes
/// and ignoring the groups above it puts every stall in the wrong place, so the transforms have to be
/// composed rather than skipped.
/// </remarks>
public readonly record struct SvgTransform(double A, double B, double C, double D, double E, double F)
{
    public static readonly SvgTransform Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>This transform applied after <paramref name="inner"/> — the order a parent wraps a child in.</summary>
    public SvgTransform Compose(SvgTransform inner) => new(
        (A * inner.A) + (C * inner.B),
        (B * inner.A) + (D * inner.B),
        (A * inner.C) + (C * inner.D),
        (B * inner.C) + (D * inner.D),
        (A * inner.E) + (C * inner.F) + E,
        (B * inner.E) + (D * inner.F) + F);

    public (double X, double Y) Apply(double x, double y) =>
        ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    private static readonly Regex FunctionPattern = new(
        @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads an SVG <c>transform</c> attribute. Functions apply right to left, as SVG specifies.
    /// Anything unrecognised — including the skews, which turn a rectangle into something that is no
    /// longer one — is reported rather than silently treated as identity.
    /// </summary>
    public static SvgTransform Parse(string? attribute, out bool unsupported)
    {
        unsupported = false;
        if (string.IsNullOrWhiteSpace(attribute))
        {
            return Identity;
        }

        var result = Identity;
        foreach (Match match in FunctionPattern.Matches(attribute))
        {
            var numbers = SvgNumbers.Parse(match.Groups[2].Value);
            var step = match.Groups[1].Value switch
            {
                "matrix" when numbers.Count == 6 => new SvgTransform(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]),
                "translate" when numbers.Count >= 1 => new SvgTransform(1, 0, 0, 1, numbers[0], numbers.Count > 1 ? numbers[1] : 0),
                "scale" when numbers.Count >= 1 => new SvgTransform(numbers[0], 0, 0, numbers.Count > 1 ? numbers[1] : numbers[0], 0, 0),
                "rotate" when numbers.Count is 1 or 3 => Rotation(numbers),
                _ => (SvgTransform?)null,
            };

            if (step is null)
            {
                unsupported = true;
                continue;
            }

            result = result.Compose(step.Value);
        }

        return result;
    }

    private static SvgTransform Rotation(IReadOnlyList<double> numbers)
    {
        var radians = numbers[0] * Math.PI / 180d;
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
        var turn = new SvgTransform(cos, sin, -sin, cos, 0, 0);
        if (numbers.Count == 1)
        {
            return turn;
        }

        // rotate(a cx cy) is a turn about a point: move it to the origin, turn, move it back.
        var (cx, cy) = (numbers[1], numbers[2]);
        return new SvgTransform(1, 0, 0, 1, cx, cy)
            .Compose(turn)
            .Compose(new SvgTransform(1, 0, 0, 1, -cx, -cy));
    }
}

/// <summary>
/// Reads the numbers out of an SVG attribute. They are separated by commas, spaces or nothing at all
/// (a minus sign starts a new number), and are always written with a dot however the reader's culture
/// spells a decimal point.
/// </summary>
public static class SvgNumbers
{
    private static readonly Regex Pattern = new(
        @"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<double> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var values = new List<double>();
        foreach (Match match in Pattern.Matches(text))
        {
            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>One number from an attribute like <c>width="1200.5"</c>, ignoring any unit suffix.</summary>
    public static double? Single(string? text)
    {
        var values = Parse(text);
        return values.Count > 0 ? values[0] : null;
    }
}
