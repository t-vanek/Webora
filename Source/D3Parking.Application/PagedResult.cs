namespace D3Parking.Application;

/// <summary>
/// One page of a longer list, with the total so the UI can render a pager. Carrying the total is the
/// point: a query that just returns the first N rows cannot tell "that is all of them" apart from
/// "there are more, you are not being shown them".
/// </summary>
/// <remarks>
/// Not under Parking, where it started: the account audit trail pages through the same envelope, and
/// nothing about "a page of things" belongs to one feature.
/// </remarks>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int PageIndex, int PageSize)
{
    public static PagedResult<T> Empty(int pageSize) => new([], 0, 0, pageSize);
}
