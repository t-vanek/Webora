using System.Globalization;

namespace D3Parking.Application.Parking.Maps;

/// <summary>
/// Continues a numbered label: "428" → "429", "H1" → "H2", "A-01" → "A-02". Pure, so the editor can
/// preview a whole row's numbering before anything is created.
/// </summary>
/// <remarks>
/// The trailing digit run is what advances, and the zero padding it was written with is kept — a plan
/// numbered 01…09 must not turn into 010. A label with no trailing digits ("VISITOR") has no successor
/// and yields null rather than an invented one; the row is still drawn, its stalls just arrive unnamed.
/// </remarks>
public static class MapLabelSequence
{
    public static string? Next(string? label, int step = 1)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var text = label.Trim();
        var end = text.Length;
        var start = end;
        while (start > 0 && char.IsAsciiDigit(text[start - 1]))
        {
            start--;
        }

        if (start == end)
        {
            return null;
        }

        var digits = text[start..end];
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            // Longer than long.MaxValue — not a number anyone is numbering stalls with.
            return null;
        }

        var next = number + step;
        if (next < 0)
        {
            return null;
        }

        return string.Concat(
            text.AsSpan(0, start),
            next.ToString(CultureInfo.InvariantCulture).PadLeft(digits.Length, '0'));
    }
}
