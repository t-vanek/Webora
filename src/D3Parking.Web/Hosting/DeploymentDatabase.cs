using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Web.Hosting;

public sealed record SchemaState(string[] Applied, string[] Pending, string[] Risky, string Target);

public static class DeploymentDatabase
{
    public static async Task<SchemaState> InspectAsync(D3ParkingDbContext db, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToArray();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (!IsPrefix(known, applied))
            throw new InvalidOperationException("Database migration history differs from this release. Database rollback is never automatic.");
        var pending = known.Skip(applied.Length).ToArray();
        var assembly = db.GetService<IMigrationsAssembly>();
        // Treat raw SQL and every operation other than additive create/index as review-required.
        // EF's IsDestructiveChange alone misses UPDATE/DELETE statements in SqlOperation.
        var risky = pending.Where(id => assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!)
            .UpOperations.Any(o => o is not (Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation
                or Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation
                or Microsoft.EntityFrameworkCore.Migrations.Operations.CreateSequenceOperation
                or Microsoft.EntityFrameworkCore.Migrations.Operations.EnsureSchemaOperation
                or Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation
                or Microsoft.EntityFrameworkCore.Migrations.Operations.AddForeignKeyOperation))).ToArray();
        return new(applied, pending, risky, known.LastOrDefault() ?? "none");
    }

    public static bool IsPrefix(IReadOnlyList<string> known, IReadOnlyList<string> applied) =>
        applied.Count <= known.Count && known.Take(applied.Count).SequenceEqual(applied, StringComparer.Ordinal);

    public static async Task RequireCurrentSchemaAsync(D3ParkingDbContext db, CancellationToken ct)
    {
        var state = await InspectAsync(db, ct);
        if (state.Pending.Length != 0) throw new InvalidOperationException($"Pending database migrations: {state.Pending.Length}. Run D3Parking.ps1 -Action Update before starting the application.");
        // Query real model columns, not only SELECT 1 or migration-history rows.
        await db.ParkingSettings.AsNoTracking().Select(s => new { s.Id, s.ReservationTimeMode }).Take(1).ToListAsync(ct);
        await db.Users.AsNoTracking().Select(u => new { u.Id, u.Status }).Take(1).ToListAsync(ct);
    }

    public static async Task BackupAsync(D3ParkingDbContext db, string path, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1800;
        command.CommandText = "BACKUP DATABASE " + QuoteIdentifier(connection.Database)
            + " TO DISK = @path WITH COPY_ONLY, CHECKSUM; RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM;";
        command.Parameters.Add(new SqlParameter("@path", path));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string QuoteIdentifier(string value) => "[" + value.Replace("]", "]]") + "]";
}
