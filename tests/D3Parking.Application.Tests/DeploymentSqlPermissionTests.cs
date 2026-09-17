using System.ComponentModel;
using System.Diagnostics;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Web.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture, NonParallelizable]
public class DeploymentSqlPermissionTests
{
    [Test]
    public async Task Deployment_requires_backup_verification_permission_and_minimal_grant_allows_repeated_backups()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires Windows SQL Server LocalDB; uses a new isolated instance.");
        var id = Guid.NewGuid().ToString("N");
        var instance = "D3Parking_Permissions_" + id;
        var database = "D3Parking_Permissions_" + id;
        var login = "Deployment_" + id;
        var root = Path.Combine(Path.GetTempPath(), "d3parking-sql-permissions-" + id);
        Directory.CreateDirectory(root);
        var created = false;
        try
        {
            try { await LocalDbAsync("create", instance); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
            {
                Assert.Ignore("SQL Server LocalDB is not installed or SqlLocalDB.exe is not on PATH.");
            }
            created = true;
            await LocalDbAsync("start", instance);
            var connectionString = new SqlConnectionStringBuilder
            {
                DataSource = @"(localdb)\" + instance,
                InitialCatalog = "master", IntegratedSecurity = true,
                Encrypt = SqlConnectionEncryptOption.Optional, Pooling = false,
            };
            await using var owner = new SqlConnection(connectionString.ConnectionString);
            await owner.OpenAsync();
            // Every SQL identifier is generated here; all physical files belong to this test.
            await ExecuteAsync(owner, $"""
                CREATE DATABASE [{database}]
                  ON PRIMARY (NAME=N'{database}', FILENAME=N'{Literal(Path.Combine(root, "data.mdf"))}')
                  LOG ON (NAME=N'{database}_log', FILENAME=N'{Literal(Path.Combine(root, "data.ldf"))}');
                """);
            await ExecuteAsync(owner, $"""
                CREATE LOGIN [{login}] WITH PASSWORD=N'Local-Test-{id}!', CHECK_POLICY=OFF;
                USE [{database}];
                CREATE USER [{login}] FOR LOGIN [{login}];
                ALTER ROLE db_owner ADD MEMBER [{login}];
                USE master;
                """);
            connectionString.InitialCatalog = database;
            await using var restricted = new SqlConnection(connectionString.ConnectionString);
            await restricted.OpenAsync();
            await ExecuteAsync(restricted, $"EXECUTE AS LOGIN = N'{login}'");
            Assert.That(await ScalarAsync(restricted, "SELECT IS_SRVROLEMEMBER('sysadmin')"), Is.Zero);
            Assert.That(await ScalarAsync(restricted, "SELECT IS_MEMBER('db_owner')"), Is.EqualTo(1));
            var denied = Assert.ThrowsAsync<InvalidOperationException>(
                () => DeploymentSqlPermissions.CheckAsync(restricted, maintenance: true, CancellationToken.None));
            Assert.That(denied!.Message, Does.Contain("CREATE DATABASE in master").And.Contain("RESTORE VERIFYONLY"));
            Assert.That(restricted.Database, Is.EqualTo(database), "The permission check must restore the target database even on failure.");
            // Runtime DML access does not need the additional deployment permission.
            await DeploymentSqlPermissions.CheckAsync(restricted, maintenance: false, CancellationToken.None);

            await using var db = new D3ParkingDbContext(new DbContextOptionsBuilder<D3ParkingDbContext>()
                .UseSqlServer(restricted).Options);
            var deniedBackup = Path.Combine(root, "before-grant.bak");
            var verifyError = Assert.ThrowsAsync<SqlException>(
                () => DeploymentDatabase.BackupAsync(db, deniedBackup, CancellationToken.None));
            Assert.That(verifyError!.Number, Is.EqualTo(262), "BACKUP succeeds, but VERIFYONLY must reject this restricted login.");
            Assert.That(File.Exists(deniedBackup), Is.True);

            // Grant only the documented additional permission, never sysadmin/dbcreator.
            await ExecuteAsync(owner, $"""
                CREATE USER [{login}] FOR LOGIN [{login}];
                GRANT CREATE DATABASE TO [{login}];
                """);
            await ExecuteAsync(restricted, $"REVERT; EXECUTE AS LOGIN = N'{login}';");
            for (var cycle = 0; cycle < 2; cycle++)
            {
                await DeploymentSqlPermissions.CheckAsync(restricted, maintenance: true, CancellationToken.None);
                Assert.That(restricted.Database, Is.EqualTo(database));
                Assert.That(await ScalarAsync(restricted, "SELECT IS_SRVROLEMEMBER('sysadmin')"), Is.Zero);
                Assert.That(await ScalarAsync(restricted, "SELECT IS_SRVROLEMEMBER('dbcreator')"), Is.Zero);
                var backup = Path.Combine(root, $"verified-{cycle}.bak");
                await DeploymentDatabase.BackupAsync(db, backup, CancellationToken.None);
                Assert.That(new FileInfo(backup).Length, Is.GreaterThan(0));
            }
        }
        finally
        {
            // Only this newly generated LocalDB instance may be stopped/deleted.
            if (created)
            {
                await LocalDbAsync("stop", instance, "-k");
                await LocalDbAsync("delete", instance);
            }
            var fullRoot = Path.GetFullPath(root);
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(fullRoot) != "d3parking-sql-permissions-" + id)
                throw new InvalidOperationException("Refusing to delete outside this test's temporary directory.");
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static string Literal(string value) => value.Replace("'", "''");

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task LocalDbAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("SqlLocalDB.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("SQL Server LocalDB management command timed out.");
        }
        Assert.That(process.ExitCode, Is.Zero, await errors + await output);
    }
}
