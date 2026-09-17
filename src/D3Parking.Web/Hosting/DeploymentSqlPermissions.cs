using Microsoft.Data.SqlClient;

namespace D3Parking.Web.Hosting;

internal static class DeploymentSqlPermissions
{
    internal static async Task CheckAsync(string connectionString, bool maintenance, CancellationToken ct)
    {
        await using var sql = new SqlConnection(connectionString);
        await sql.OpenAsync(ct);
        await CheckAsync(sql, maintenance, ct);
    }

    // Keep the existing session/security context so callers can test the effective permissions
    // of a restricted deployment login without using or persisting its password.
    internal static async Task CheckAsync(SqlConnection sql, bool maintenance, CancellationToken ct)
    {
        await using var command = sql.CreateCommand();
        command.CommandText = maintenance
            ? "SELECT CASE WHEN IS_MEMBER('db_owner')=1 THEN 1 ELSE 0 END"
            : "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'SELECT')=1 AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT')=1 AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE')=1 AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE')=1 THEN 1 ELSE 0 END";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 1)
            throw new InvalidOperationException(maintenance
                ? "Deployment SQL user needs db_owner on this dedicated database."
                : "Application SQL user needs SELECT, INSERT, UPDATE and DELETE on this database.");

        if (!maintenance) return;

        // BACKUP is allowed to db_owner, but RESTORE VERIFYONLY also requires effective
        // CREATE DATABASE permission in master. Check before the installer stops the service.
        var database = sql.Database;
        await sql.ChangeDatabaseAsync("master", ct);
        try
        {
            command.CommandText = "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE DATABASE')=1 THEN 1 ELSE 0 END";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 1)
                throw new InvalidOperationException("Deployment SQL login needs CREATE DATABASE in master for RESTORE VERIFYONLY. Ask the DBA to grant this permission before deployment.");
        }
        finally
        {
            await sql.ChangeDatabaseAsync(database, CancellationToken.None);
        }
    }
}
