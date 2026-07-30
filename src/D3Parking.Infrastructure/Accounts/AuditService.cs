using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Accounts;
using D3Parking.Domain.Accounts;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Accounts;

public sealed class AuditService(IDbContextFactory<D3ParkingDbContext> dbContextFactory) : IAuditService
{
    private const int MaxLimit = 500;

    public async Task<IReadOnlyList<AuditLogEntry>> SearchAsync(
        string? search,
        AccountAuditEventType? type,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Left join: an event outlives the account it describes (deletion is itself auditable), and
        // dropping those rows would quietly hide exactly the history most worth keeping.
        var query = from audit in dbContext.AccountAuditEvents.AsNoTracking()
                    join user in dbContext.Users on audit.UserId equals user.Id into matches
                    from user in matches.DefaultIfEmpty()
                    select new { audit, user };

        if (type is { } eventType)
        {
            query = query.Where(row => row.audit.Type == eventType);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(row =>
                (row.user != null && EF.Functions.Like(row.user.Email!, term))
                || (row.user != null && row.user.DisplayName != null && EF.Functions.Like(row.user.DisplayName, term))
                || (row.audit.Detail != null && EF.Functions.Like(row.audit.Detail, term)));
        }

        var rows = await query
            .OrderByDescending(row => row.audit.OccurredAtUtc)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new AuditLogEntry(
                row.audit.Id,
                row.audit.UserId,
                row.user?.Email,
                row.user?.DisplayName,
                row.audit.Type,
                row.audit.OccurredAtUtc,
                row.audit.Actor,
                row.audit.Detail))
            .ToArray();
    }
}
