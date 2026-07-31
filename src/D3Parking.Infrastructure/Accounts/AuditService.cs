using Microsoft.EntityFrameworkCore;
using D3Parking.Application;
using D3Parking.Application.Accounts;
using D3Parking.Domain.Accounts;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Accounts;

public sealed class AuditService(IDbContextFactory<D3ParkingDbContext> dbContextFactory) : IAuditService
{
    public async Task<PagedResult<AuditLogEntry>> SearchAsync(
        string? search,
        AccountAuditEventType? type,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // Clamped here, not trusted from the caller: the page size decides how much a single request
        // can ask the database to materialise.
        var size = Math.Clamp(pageSize, 1, 100);
        var index = Math.Max(0, pageIndex);

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

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResult<AuditLogEntry>.Empty(size);
        }

        // A page past the end walks back to the last page that exists rather than rendering an empty
        // table — the filters above the grid narrow the trail under the reader's feet.
        index = Math.Min(index, (total - 1) / size);

        // The instant alone is not a total order: events written by one operation share it to the
        // tick, and Skip/Take over a tie can drop a row from one page and repeat it on the next. The
        // id breaks the tie — arbitrary, but stable, which is the whole requirement.
        var rows = await query
            .OrderByDescending(row => row.audit.OccurredAtUtc)
            .ThenByDescending(row => row.audit.Id)
            .Skip(index * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        var items = rows
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

        return new PagedResult<AuditLogEntry>(items, total, index, size);
    }
}
