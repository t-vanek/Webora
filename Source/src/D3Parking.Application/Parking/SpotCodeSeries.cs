using System.Globalization;

namespace D3Parking.Application.Parking;

/// <summary>
/// Pure expansion of a spot-series pattern (sections × numeric range) into concrete codes,
/// e.g. sections "A, B" with range 1–3 and pad width 2 → A-01 … B-03. No I/O — collisions with
/// existing spots are the batch service's concern, so this stays trivially unit-testable.
/// </summary>
public static class SpotCodeSeries
{
    /// <summary>Upper bound on one generated batch — a typo guard, not a capacity limit.</summary>
    public const int MaxBatchSize = 500;

    public const int MaxNumber = 99_999;
    public const int MaxPadWidth = 5;

    public static SpotSeriesExpansion Expand(string? sections, string? separator, int from, int to, int padWidth)
    {
        if (from < 0 || to > MaxNumber || from > to || padWidth is < 0 or > MaxPadWidth)
        {
            return SpotSeriesExpansion.Failure("Parking_Error_SeriesRange");
        }

        var sectionList = (sections ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // No sections at all still makes a series — plain numeric codes without a separator.
        if (sectionList.Count == 0)
        {
            sectionList.Add(string.Empty);
        }

        var count = (long)sectionList.Count * (to - from + 1);
        if (count > MaxBatchSize)
        {
            return SpotSeriesExpansion.Failure("Parking_Error_SeriesTooLarge");
        }

        separator ??= string.Empty;
        var codes = new List<string>((int)count);
        foreach (var section in sectionList)
        {
            for (var n = from; n <= to; n++)
            {
                var number = n.ToString(CultureInfo.InvariantCulture).PadLeft(padWidth, '0');
                codes.Add(section.Length == 0 ? number : section + separator + number);
            }
        }

        return new SpotSeriesExpansion(codes, null);
    }
}

/// <summary>Result of expanding a series pattern: the codes, or the error key when the pattern is unusable.</summary>
public sealed record SpotSeriesExpansion(IReadOnlyList<string> Codes, string? ErrorKey)
{
    public bool Succeeded => ErrorKey is null;

    public static SpotSeriesExpansion Failure(string errorKey) => new([], errorKey);
}
