using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using D3Parking.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture, NonParallelizable]
public class PublishedReleaseTests
{
    [Test]
    public async Task Published_executable_starts_and_restarts_without_SDK_and_keeps_shared_keys()
    {
        var app = Environment.GetEnvironmentVariable("D3PARKING_RELEASE_APP");
        var sql = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(sql))
            Assert.Ignore("Set D3PARKING_RELEASE_APP to the extracted app directory and ConnectionStrings__SqlServer to a local test SQL server.");
        var connection = new SqlConnectionStringBuilder(sql) { InitialCatalog = $"D3Parking_PublishAudit_{Guid.NewGuid():N}" }.ConnectionString;
        var root = Path.Combine(Path.GetTempPath(), $"d3parking-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "secrets"));
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Audit Data Protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = Path.Combine(root, "secrets", "test.pfx");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, "audit-only"));
        File.WriteAllText(Path.Combine(root, "secrets", "secrets.json"), "// Komentované soubory vytváří instalační průvodce.\n{}");
        await using var db = new D3ParkingDbContext(new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(connection).Options);
        string[]? originalKeys = null;
        try
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                using var port = new TcpListener(IPAddress.Loopback, 0);
                port.Start();
                var url = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}";
                port.Stop();
                File.WriteAllText(Path.Combine(root, "config", "appsettings.json"), "// Sdílené nastavení s českými vysvětlivkami musí jít načíst i po restartu.\n" + JsonSerializer.Serialize(new
                {
                    ConnectionStrings = new { SqlServer = connection }, Account = new { BaseUrl = url },
                    IdentitySeed = new { AdminEmail = "release@test.local", AdminPassword = "Audit-Password-938!" },
                    DataProtection = new { Certificate = new { Path = pfx, Password = "audit-only" } },
                }));
                var start = new ProcessStartInfo(Path.Combine(app!, "D3Parking.Web.exe"))
                {
                    WorkingDirectory = app, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (var argument in new[] { "--contentRoot", app!, "--environment", "Development", "--Deployment:InstallPath", root, "--urls", url })
                    start.ArgumentList.Add(argument);
                start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
                start.Environment["DOTNET_ROOT"] = Path.Combine(root, "no-sdk-installed");
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                try
                {
                    using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };
                    var ready = false;
                    for (var attempt = 0; attempt < 90 && !process.HasExited; attempt++)
                    {
                        try { ready = (await http.GetAsync("/health/ready")).IsSuccessStatusCode; }
                        catch (HttpRequestException) { }
                        catch (TaskCanceledException) { }
                        if (ready) break;
                        await Task.Delay(500);
                    }
                    Assert.That(ready, Is.True, process.HasExited ? await errors + await output : "Published process never became ready.");
                    var version = JsonDocument.Parse(await http.GetStringAsync("/version")).RootElement;
                    Assert.That(version.GetProperty("environment").GetString(), Is.EqualTo("Development"));
                    Assert.That(await http.GetStringAsync("/login"), Does.Contain("Input.Email"));
                    Assert.That((await http.GetAsync("/_framework/blazor.web.js")).IsSuccessStatusCode, Is.True);
                    var keys = Directory.GetFiles(Path.Combine(root, "data", "keys"), "key-*.xml");
                    Assert.That(keys, Is.Not.Empty);
                    Assert.That(File.ReadAllText(keys[0]), Does.Contain("encryptedSecret"));
                    if (originalKeys is not null) Assert.That(keys, Is.EquivalentTo(originalKeys));
                    originalKeys = keys;
                }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await output; await errors;
                }
            }
            Assert.That(Directory.GetFiles(Path.Combine(root, "logs"), "application-*.log"), Is.Not.Empty);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
            Directory.Delete(root, recursive: true); // Only this test's freshly generated directory.
        }
    }
}
