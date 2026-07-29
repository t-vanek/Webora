using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace D3Parking.Infrastructure.Persistence;

/// <summary>
/// Recovery strategies for writes guarded by the rowversion tokens (see the entity configs in
/// <see cref="D3ParkingDbContext"/>). A <see cref="DbUpdateConcurrencyException"/> here always
/// means "someone else committed first, your snapshot is stale" — never data corruption — so the
/// correct reaction is to re-read and decide again, not to fail the request.
/// </summary>
internal static class OptimisticConcurrency
{
    private const int MaxAttempts = 3;

    /// <summary>
    /// Runs a user-facing operation again from scratch when a concurrent writer invalidated its
    /// snapshot. The operation must create its own DbContext per invocation, so a retry really
    /// re-reads current state — its status guards then resolve the race (typically to a friendly
    /// "already cancelled/changed" failure instead of a double refund or a lost update).
    /// </summary>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested && IsLostRace(ex))
            {
            }
        }
    }

    /// <summary>
    /// A duplicate key (2601/2627) from a concurrent insert of the same row — e.g. two requests
    /// both creating the user's ParkerScore, or two releases landing the same (SpotId, Date).
    /// </summary>
    public static bool IsUniqueViolation(Exception ex) =>
        ex.GetBaseException() is SqlException { Number: 2601 or 2627 };

    // "The other writer got there first, nothing of ours committed": a stale rowversion, a
    // duplicate key from a concurrent first-touch insert, or losing a deadlock between two
    // serializable transactions (SQL Server rolls the victim back whole). A fresh attempt reads
    // what the winner wrote and decides again. Anything else keeps propagating. The transient
    // wrapper EF puts around such SqlExceptions is walked through via GetBaseException.
    private static bool IsLostRace(Exception ex) =>
        ex is DbUpdateConcurrencyException
        || IsUniqueViolation(ex)
        || ex.GetBaseException() is SqlException { Number: 1205 };

    /// <summary>
    /// Batch-save for maintenance jobs whose entities are independent of each other: detaches the
    /// entities a concurrent writer beat us to and saves the rest. A skipped row is not lost work —
    /// the user's own action won, and the next maintenance tick re-evaluates whatever remains.
    /// Callers must treat detached entities as "not processed" (skip their notifications).
    /// </summary>
    public static async Task SaveSkippingConflictsAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        for (; ; )
        {
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <summary>
    /// Throws away every pending (unsaved) change in the context. Used by maintenance jobs that
    /// save per work unit: when a unit's save conflicts, all of its in-memory mutations — the
    /// entity itself plus dependent rows like ledger entries and scores — must go, or they would
    /// leak into the next unit's save.
    /// </summary>
    public static void DiscardPendingChanges(DbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
