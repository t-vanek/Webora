namespace D3Parking.Application.Parking;

/// <summary>
/// Natural ordering for spot codes: digit runs compare as numbers, everything else
/// case-insensitively — so D3-2 sorts before D3-10 instead of the lexicographic
/// D3-1, D3-10, D3-2 a database string sort produces.
/// </summary>
public sealed class SpotCodeComparer : IComparer<string?>
{
    public static readonly SpotCodeComparer Instance = new();

    private SpotCodeComparer() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                // Compare the runs as numbers without parsing (codes may exceed any int):
                // strip leading zeros, then a longer run is a bigger number, ties compare digit-wise.
                var numX = x.AsSpan(startX, i - startX).TrimStart('0');
                var numY = y.AsSpan(startY, j - startY).TrimStart('0');
                if (numX.Length != numY.Length)
                {
                    return numX.Length - numY.Length;
                }

                var numCompare = numX.CompareTo(numY, StringComparison.Ordinal);
                if (numCompare != 0)
                {
                    return numCompare;
                }

                // Same numeric value ("1" vs "01"): fewer leading zeros first, for a stable order.
                var zeroCompare = (i - startX) - (j - startY);
                if (zeroCompare != 0)
                {
                    return zeroCompare;
                }
            }
            else
            {
                var charCompare = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (charCompare != 0)
                {
                    return charCompare;
                }

                i++;
                j++;
            }
        }

        return (x.Length - i) - (y.Length - j);
    }
}
