using System.Text.RegularExpressions;

namespace D3Parking.Application.Parking.Maps;

/// <summary>
/// The corner points of every subpath of a path made only of straight lines, or null when the path
/// contains anything else.
/// </summary>
/// <remarks>
/// Two things about real exports drive this. First, a PDF converted to SVG contains no
/// <c>&lt;rect&gt;</c> at all — every box is a path of move-and-line commands, so a reader that only
/// understands rectangles finds nothing in the format it is most likely to be handed. Second, those
/// converters habitually put <em>many</em> boxes in one path, each as its own subpath: treating a
/// path as a single shape and giving up at the second <c>M</c> loses a whole row of stalls at a time,
/// which is exactly what this used to do.
///
/// Only the straight-line commands are implemented. The moment a curve appears the shape is not a
/// stall, and saying so by returning null — so the caller can count it — is the whole answer.
/// </remarks>
public static class SvgPathPoints
{
    private static readonly Regex CommandPattern = new(
        @"([MmLlHhVvZzCcSsQqTtAa])([^MmLlHhVvZzCcSsQqTtAa]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Guard against a pathological <c>d</c> attribute. A plan of five hundred stalls fits.</summary>
    private const int MaxPoints = 20_000;

    /// <summary>
    /// One entry per subpath, in the order they are drawn. Null means the path holds a command this
    /// reader does not implement, which is the caller's cue to count the loss rather than ignore it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(double X, double Y)>>? Read(string? d)
    {
        if (string.IsNullOrWhiteSpace(d))
        {
            return null;
        }

        var subpaths = new List<IReadOnlyList<(double X, double Y)>>();
        var current = new List<(double X, double Y)>();
        double x = 0, y = 0;
        var total = 0;

        void Close()
        {
            if (current.Count > 0)
            {
                subpaths.Add(current);
                current = [];
            }
        }

        foreach (Match match in CommandPattern.Matches(d))
        {
            var command = match.Groups[1].Value[0];
            var numbers = SvgNumbers.Parse(match.Groups[2].Value);
            var relative = char.IsLower(command);

            void Add(double px, double py)
            {
                (x, y) = (px, py);
                current.Add((px, py));
                total++;
            }

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    if (numbers.Count < 2)
                    {
                        return null;
                    }

                    // A move ends the subpath before it and starts the next one. This is the line
                    // that turns "one row of stalls, lost" into "one row of stalls, read".
                    Close();
                    Add(relative ? x + numbers[0] : numbers[0], relative ? y + numbers[1] : numbers[1]);

                    // Further pairs after a moveto are implicit linetos, per the SVG grammar.
                    for (var i = 2; i + 1 < numbers.Count; i += 2)
                    {
                        Add(relative ? x + numbers[i] : numbers[i], relative ? y + numbers[i + 1] : numbers[i + 1]);
                    }

                    break;

                case 'L':
                    for (var i = 0; i + 1 < numbers.Count; i += 2)
                    {
                        Add(relative ? x + numbers[i] : numbers[i], relative ? y + numbers[i + 1] : numbers[i + 1]);
                    }

                    break;

                case 'H':
                    foreach (var value in numbers)
                    {
                        Add(relative ? x + value : value, y);
                    }

                    break;

                case 'V':
                    foreach (var value in numbers)
                    {
                        Add(x, relative ? y + value : value);
                    }

                    break;

                case 'Z':
                    // Closing is implied by the subpath being a quadrilateral; the point itself would
                    // only repeat the first one, which the caller already tolerates. The pen returns
                    // to the subpath's start, which matters for a relative move that follows.
                    if (current.Count > 0)
                    {
                        (x, y) = current[0];
                    }

                    break;

                default:
                    // A curve, an arc, anything else: not a stall, and the caller has to hear about it.
                    return null;
            }

            if (total > MaxPoints)
            {
                return null;
            }
        }

        Close();
        return subpaths.Count > 0 ? subpaths : null;
    }
}
