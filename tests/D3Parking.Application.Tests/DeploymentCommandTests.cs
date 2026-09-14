using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture, NonParallelizable]
public class DeploymentCommandTests
{
    [Test]
    public async Task Preflight_is_read_only_and_upgrade_requires_unchanged_schema_and_verified_backup()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured)) Assert.Ignore("Requires SQL Server and its service access to the local temporary backup directory.");
        var connection = new SqlConnectionStringBuilder(configured)
        { InitialCatalog = $"D3Parking_DeploymentAudit_{Guid.NewGuid():N}" }.ConnectionString;
        // This native LocalDB test exercises SQL BACKUP/VERIFYONLY and migrations. It does not
        // claim to test production TLS, service identities or a remote SQL server's filesystem.
        var root = Path.Combine(Path.GetTempPath(), $"d3parking-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "secrets"));
        var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
        File.WriteAllText(Path.Combine(root, "secrets", "deployment.json"), "// Připojení pro správce nasazení, vytvořené průvodcem.\n" + JsonSerializer.Serialize(new
        { ConnectionStrings = new { SqlServer = connection } }));
        void Policy(string directory) => File.WriteAllText(Path.Combine(root, "config", "deployment.json"),
            "// Složka záloh leží na SQL serveru.\n" + JsonSerializer.Serialize(new { SqlBackupDirectory = directory, ApprovedMigrations = Array.Empty<string>() }));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var shutdown = new CancellationTokenSource();
        var smtp = ServeSmtpAsync(listener, shutdown.Token);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = connection, ["IdentitySeed:AdminEmail"] = "bootstrap@test.local",
            ["IdentitySeed:AdminPassword"] = "Audit-Password-938!", ["Smtp:Host"] = "127.0.0.1",
            ["Smtp:Port"] = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(),
            ["Smtp:Authentication"] = "None", ["Smtp:Security"] = "None", ["Smtp:TimeoutSeconds"] = "5",
        });
        await using var db = new D3ParkingDbContext(new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(connection).Options);
        async Task<JsonElement> Run(string command, int expectedExit)
        {
            var report = Path.Combine(root, $"{Guid.NewGuid():N}.json");
            builder.Configuration["deployment-report"] = report;
            Assert.That(await DeploymentCommands.RunAsync(builder, command, root), Is.EqualTo(expectedExit),
                File.Exists(report) ? File.ReadAllText(report) : "No deployment report");
            return JsonDocument.Parse(File.ReadAllText(report)).RootElement.Clone();
        }
        try
        {
            await db.GetService<IRelationalDatabaseCreator>().CreateAsync();
            Policy(backups);
            var preflight = await Run("preflight", 0);
            Assert.That(preflight.GetProperty("schema").GetProperty("pending").GetArrayLength(), Is.GreaterThan(30));
            Assert.That(await db.Database.GetAppliedMigrationsAsync(), Is.Empty);
            Assert.That(Directory.GetFiles(backups), Is.Empty);

            builder.Configuration["deployment-expected-schema"] = "wrong";
            var stale = await Run("upgrade", 1);
            Assert.That(stale.GetProperty("databaseMayHaveChanged").GetBoolean(), Is.False);
            Assert.That(stale.GetProperty("phase").GetString(), Is.EqualTo("migration approval"));

            builder.Configuration["deployment-expected-schema"] = "none";
            Policy(Path.Combine(backups, "does-not-exist"));
            var failedBackup = await Run("upgrade", 1);
            Assert.That(failedBackup.GetProperty("phase").GetString(), Is.EqualTo("database backup"));
            Assert.That(failedBackup.GetProperty("databaseMayHaveChanged").GetBoolean(), Is.False);
            Assert.That(await db.Database.GetAppliedMigrationsAsync(), Is.Empty);

            Policy(backups);
            var upgraded = await Run("upgrade", 0);
            Assert.That(upgraded.GetProperty("databaseMayHaveChanged").GetBoolean(), Is.True);
            Assert.That(new FileInfo(upgraded.GetProperty("backup").GetString()!).Length, Is.GreaterThan(0));
            await DeploymentDatabase.RequireCurrentSchemaAsync(db, CancellationToken.None);
        }
        finally
        {
            shutdown.Cancel();
            listener.Stop();
            try { await smtp; } catch (OperationCanceledException) { }
            await db.Database.EnsureDeletedAsync();
            Directory.Delete(root, recursive: true); // The GUID directory created by this test only.
        }
    }

    private static async Task ServeSmtpAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 localhost audit SMTP");
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("QUIT", StringComparison.Ordinal)) { await writer.WriteLineAsync("221 bye"); break; }
                if (line.StartsWith("EHLO", StringComparison.Ordinal) || line.StartsWith("HELO", StringComparison.Ordinal))
                    await writer.WriteLineAsync("250 localhost");
                else throw new InvalidOperationException("Preflight must only connect/greet/quit; no messages may be sent.");
            }
        }
    }
}
