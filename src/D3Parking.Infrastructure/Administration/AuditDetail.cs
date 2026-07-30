namespace D3Parking.Infrastructure.Administration;

/// <summary>Shared formatting for the free-text detail stored on an audit event.</summary>
internal static class AuditDetail
{
    private const int MaxLength = 1024;

    /// <summary>
    /// Clamps to the Detail column's nvarchar(1024). A group given the whole catalog, or a role
    /// recomposed from a dozen groups, would otherwise overrun it and fail the write.
    /// </summary>
    public static string Truncate(string detail) =>
        detail.Length <= MaxLength ? detail : detail[..(MaxLength - 3)] + "...";
}
