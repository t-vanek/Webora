using System.Text.RegularExpressions;

namespace D3Parking.Application.Parking.Maps;

/// <summary>
/// The corner points of a path made only of straight lines, or null when it is made of anything else.
/// </summary>
/// <remarks>
/// A PDF converted to SVG has no rectangles in it: every box comes out as a path of move-and-line
/// commands, so a reader that only understands <c>&lt;rect&gt;</c> finds nothing in the file it was
/// most likely to be given. Only the straight-line commands are implemented — the moment a curve
/// appears the shape is not a stall, and saying so by returning null is the whole answer.
/// </remarks>
public static class SvgPathPoints
{
    private static readonly Regex CommandPattern = new(
        @"([MmLlHhVvZzCcSsQqTtAa])([^MmLlHhVvZzCcSsQqTtAa]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Guard against a pathological <c>d</c> attribute; a stall needs five points at most.</summary>
    private const int MaxPoints = 64;

    public static IReadOnlyList<(double X, double Y)>? Read(string? d)
    {
        if (string.IsNullOrWhiteSpace(d))
        {
            return null;
        }

        var points = new List<(double X, double Y)>();
        double x = 0, y = 0;
        var started = false;

        foreach (Match match in CommandPattern.Matches(d))
        {
            var command = match.Groups[1].Value[0];
            var numbers = SvgNumbers.Parse(match.Groups[2].Value);
            var relative = char.IsLower(command);

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    // A second move starts another subpath. One stall is one subpath; a file that
                    // draws several in one path is not something to guess about.
                    if (started)
                    {
                        return null;
                    }

                    if (numbers.Count < 2)
                    {
                        return null;
                    }

                    (x, y) = relative ? (x + numbers[0], y + numbers[1]) : (numbers[0], numbers[1]);
                    points.Add((x, y));
                    started = true;

                    // Extra pairs after a moveto are implicit linetos, per the SVG grammar.
                    for (var i = 2; i + 1 < numbers.Count; i += 2)
                    {
                        (x, y) = relative ? (x + numbers[i], y + numbers[i + 1]) : (numbers[i], numbers[i + 1]);
                        points.Add((x, y));
                    }

                    break;

                case 'L':
                    for (var i = 0; i + 1 < numbers.Count; i += 2)
                    {
                        (x, y) = relative ? (x + numbers[i], y + numbers[i + 1]) : (numbers[i], numbers[i + 1]);
                        points.Add((x, y));
                    }

                    break;

                case 'H':
                    foreach (var value in numbers)
                    {
                        x = relative ? x + value : value;
                        points.Add((x, y));
                    }

                    break;

                case 'V':
                    foreach (var value in numbers)
                    {
                        y = relative ? y + value : value;
                        points.Add((x, y));
                    }

                    break;

                case 'Z':
                    // Closing is implied by the shape being a quadrilateral; the point itself would
                    // only repeat the first one, which Quad already tolerates.
                    break;

                default:
                    // A curve, an arc, anything else: not a stall.
                    return null;
            }

            if (points.Count > MaxPoints)
            {
                return null;
            }
        }

        return points.Count > 0 ? points : null;
    }
}
