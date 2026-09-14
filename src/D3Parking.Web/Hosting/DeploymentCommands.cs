using System.Text.Json;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Email;
using D3Parking.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace D3Parking.Web.Hosting;

/// <summary>Offline release inspection and explicit maintenance, without starting web/background workers.</summary>
public static class DeploymentCommands
{
    public static async Task<int> RunAsync(WebApplicationBuilder builder, string command, string? root)
    {
        var reportPath = builder.Configuration["deployment-report"];
        var phase = "configuration";
        var databaseMayHaveChanged = false;
        string? backup = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        var ct = timeout.Token;
        try
        {
            // This branch needs neither secrets nor a database connection.
            if (command == "manifest")
            {
                await using var metadataDb = new D3ParkingDbContext(new DbContextOptionsBuilder<D3ParkingDbContext>()
                    .UseSqlServer("Server=unused;Database=unused").Options);
                var migrations = metadataDb.Database.GetMigrations().ToArray();
                if (builder.Configuration["deployment-sql"] is { } sqlPath)
                    await File.WriteAllTextAsync(sqlPath, metadataDb.GetService<IMigrator>().GenerateScript(
                        options: MigrationsSqlGenerationOptions.Idempotent), ct);
                WriteReport(new { success = true, release = ReleaseInformation.Read(builder.Environment.EnvironmentName), migrations }, reportPath);
                return 0;
            }
            if (command is not ("preflight" or "upgrade")) throw new InvalidOperationException("Unknown deployment command.");
            DeploymentConfiguration.Validate(builder.Configuration, builder.Environment.EnvironmentName);
            if (root is null) throw new InvalidOperationException("Deployment:InstallPath is required.");
            var deploymentSecrets = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "secrets", "deployment.json"), false, false).Build();
            var runtimeConnection = builder.Configuration.GetConnectionString("SqlServer")!;
            var maintenanceConnection = deploymentSecrets.GetConnectionString("SqlServer")
                ?? throw new InvalidOperationException("Missing SQL connection in secrets/deployment.json.");
            var runtimeSql = new SqlConnectionStringBuilder(runtimeConnection);
            var maintenanceSql = new SqlConnectionStringBuilder(maintenanceConnection);
            if (!string.Equals(runtimeSql.DataSource, maintenanceSql.DataSource, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(runtimeSql.InitialCatalog, maintenanceSql.InitialCatalog, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Runtime and deployment SQL connections must name the same server and database.");
            if (!builder.Environment.IsDevelopment() && (maintenanceSql.IntegratedSecurity || maintenanceSql.TrustServerCertificate
                    || maintenanceSql.Encrypt == SqlConnectionEncryptOption.Optional || string.IsNullOrWhiteSpace(maintenanceSql.Password)))
                throw new InvalidOperationException("Deployment SQL connection needs SQL credentials and verified encryption.");
            phase = "database";
            await using var db = new D3ParkingDbContext(new DbContextOptionsBuilder<D3ParkingDbContext>()
                .UseSqlServer(maintenanceConnection, sql => sql.CommandTimeout(1800)).Options);
            // Open explicitly: never let Migrate create a missing/mistyped production database.
            await db.Database.OpenConnectionAsync(ct);
            var schema = await DeploymentDatabase.InspectAsync(db, ct);
            await CheckPermissionsAsync(maintenanceConnection, true, ct);
            await CheckPermissionsAsync(runtimeConnection, false, ct);
            if (schema.Applied.Length > 0)
            {
                var hasAdmin = await (from user in db.Users
                    join membership in db.UserRoles on user.Id equals membership.UserId
                    join role in db.Roles on membership.RoleId equals role.Id
                    where user.Status == AccountStatus.Active && role.Name == Roles.Administrator
                    select user.Id).AnyAsync(ct);
                if (!hasAdmin) throw new InvalidOperationException("No active administrator exists. Recover administrator access before deploying.");
            }
            else if (string.IsNullOrWhiteSpace(builder.Configuration["IdentitySeed:AdminEmail"]))
                throw new InvalidOperationException("First installation requires IdentitySeed:AdminEmail and AdminPassword.");

            var policy = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "config", "deployment.json"), false, false).Build();
            var approved = policy.GetSection("ApprovedMigrations").Get<string[]>() ?? [];
            var unapproved = schema.Risky.Except(approved, StringComparer.Ordinal).ToArray();
            if (schema.Applied.Length == 0)
            {
                // The full historical chain is safe to initialize only an actually empty DB.
                await using var emptyCheck = db.Database.GetDbConnection().CreateCommand();
                emptyCheck.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0 AND name <> '__EFMigrationsHistory'";
                if (Convert.ToInt32(await emptyCheck.ExecuteScalarAsync(ct)) != 0)
                    throw new InvalidOperationException("Database has tables but no recognized migration history. Do not initialize it automatically.");
                unapproved = [];
            }
            var backupDirectory = policy["SqlBackupDirectory"];
            if (string.IsNullOrWhiteSpace(backupDirectory) || !Path.IsPathFullyQualified(backupDirectory))
                throw new InvalidOperationException("SqlBackupDirectory must be an absolute path ON THE SQL SERVER, writable by its service account.");

            phase = "smtp";
            await CheckSmtpAsync(builder.Configuration, ct);
            if (command == "preflight")
            {
                WriteReport(new { success = true, schema, unapprovedMigrations = unapproved, databaseMayHaveChanged = false,
                    release = ReleaseInformation.Read(builder.Environment.EnvironmentName) }, reportPath);
                return 0;
            }
            phase = "migration approval";
            if (unapproved.Length != 0) throw new InvalidOperationException("Review and approve pending migrations by their exact IDs before upgrade.");
            var expected = builder.Configuration["deployment-expected-schema"];
            if (expected != (schema.Applied.LastOrDefault() ?? "none"))
                throw new InvalidOperationException("Database changed since preflight; rerun deployment.");
            phase = "database lock";
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @r int;
                EXEC @r = sp_getapplock @Resource = N'D3Parking:Deployment', @LockMode = 'Exclusive',
                  @LockOwner = 'Session', @LockTimeout = 0;
                IF @r < 0 THROW 51000, 'Another database deployment is running.', 1;
                """, ct);
            // Re-read under the lock, then take a recovery point with the application stopped.
            var locked = await DeploymentDatabase.InspectAsync(db, ct);
            if (!locked.Applied.SequenceEqual(schema.Applied)) throw new InvalidOperationException("Database changed while obtaining its deployment lock.");
            phase = "database backup";
            backup = Path.Combine(backupDirectory, $"D3Parking-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.bak");
            await DeploymentDatabase.BackupAsync(db, backup, ct);
            phase = "database migration";
            if (schema.Pending.Length != 0)
            {
                databaseMayHaveChanged = true;
                await db.Database.MigrateAsync(ct);
            }
            await DeploymentDatabase.RequireCurrentSchemaAsync(db, ct);
            WriteReport(new { success = true, backup, databaseMayHaveChanged, schema = await DeploymentDatabase.InspectAsync(db, ct) }, reportPath);
            return 0;
        }
        catch (Exception ex)
        {
            // No connection strings, URLs containing tokens, credentials, or arbitrary provider messages.
            var reason = ex is InvalidOperationException ? ex.Message : $"{phase} check failed ({ex.GetType().Name}).";
            WriteReport(new { success = false, phase, reason, errorType = ex.GetType().FullName,
                sqlError = (ex as SqlException)?.Number, backup, databaseMayHaveChanged }, reportPath);
            return 1;
        }
    }

    private static async Task CheckPermissionsAsync(string connectionString, bool maintenance, CancellationToken ct)
    {
        await using var sql = new SqlConnection(connectionString);
        await sql.OpenAsync(ct);
        await using var command = sql.CreateCommand();
        command.CommandText = maintenance
            ? "SELECT CASE WHEN IS_MEMBER('db_owner')=1 THEN 1 ELSE 0 END"
            : "SELECT CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'SELECT')=1 AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT')=1 AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE')=1 AND HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE')=1 THEN 1 ELSE 0 END";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 1)
            throw new InvalidOperationException(maintenance ? "Deployment SQL user needs db_owner on this dedicated database." : "Application SQL user needs SELECT, INSERT, UPDATE and DELETE on this database.");
    }

    private static async Task CheckSmtpAsync(IConfiguration config, CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmail(config);
        await using var provider = services.BuildServiceProvider();
        var smtp = provider.GetRequiredService<IOptions<SmtpOptions>>().Value;
        using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(smtp.TimeoutSeconds));
        await client.ConnectAsync(smtp.Host, smtp.Port, smtp.Security switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.Auto,
        }, bounded.Token);
        if (smtp.Authentication == SmtpAuthMode.Basic)
            await client.AuthenticateAsync(smtp.UserName!, smtp.Password!, bounded.Token);
        if (smtp.Authentication == SmtpAuthMode.OAuth2)
            await client.AuthenticateAsync(new SaslMechanismOAuth2(smtp.UserName!,
                await provider.GetRequiredService<ISmtpAccessTokenProvider>().GetAccessTokenAsync(bounded.Token)), bounded.Token);
        await client.DisconnectAsync(true, bounded.Token);
    }

    private static void WriteReport(object report, string? path)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        if (path is null) Console.WriteLine(json);
        else File.WriteAllText(path, json);
    }
}
