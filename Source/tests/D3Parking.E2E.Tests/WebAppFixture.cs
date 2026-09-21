using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Domain.Parking;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

/// <summary>
/// Assembly-wide setup: makes sure the app is running (starting it if nothing is
/// already listening), then signs in once as the seeded admin and saves the
/// session so the authenticated specs can reuse it.
/// </summary>
[SetUpFixture]
public sealed class WebAppFixture
{
    public static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("BASE_URL") ?? $"http://127.0.0.1:{FreePort()}";

    // Unique per test process: two suites running side by side (e.g. the parallel verification
    // instance next to the IDE one) must not overwrite each other's saved session.
    public static readonly string AdminStatePath =
        Path.Combine(Path.GetTempPath(), $"d3parking-e2e-admin-state-{Environment.ProcessId}.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _app;
    private string? _testConnection;
    internal static string? IsolatedSqlConnection { get; private set; }

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        // Reuse the bundled browser if present (PLAYWRIGHT_BROWSERS_PATH); otherwise fetch it.
        Microsoft.Playwright.Program.Main(["install", "chromium"]);

        if (Environment.GetEnvironmentVariable("BASE_URL") is null)
        {
            StartApp();
            await WaitUntilUpAsync(TimeSpan.FromMinutes(3));
            await using var db = new D3ParkingDbContext(new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(_testConnection!).Options);
            db.ParkingSpots.AddRange(new ParkingSpot("E2E-BASE-01", ParkingSpotType.Standard),
                new ParkingSpot("E2E-BASE-02", ParkingSpotType.Standard));
            await db.SaveChangesAsync();
        }
        else if (!await IsUpAsync()) throw new InvalidOperationException("The explicit BASE_URL is unavailable; refusing to start another app against it.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var context = await browser.NewContextAsync(new() { BaseURL = BaseUrl, Locale = "cs-CZ" });
        var page = await context.NewPageAsync();
        await Pages.LoginAsync(page);
        await page.Locator(".wallet-chip").WaitForAsync();
        await context.StorageStateAsync(new() { Path = AdminStatePath });
        await browser.CloseAsync();
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_app is { HasExited: false })
        {
            _app.Kill(entireProcessTree: true);
            await _app.WaitForExitAsync();
            _app.Dispose();
        }
        if (_testConnection is not null)
        {
            await using var db = new D3ParkingDbContext(new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(_testConnection).Options);
            await db.Database.EnsureDeletedAsync();
        }

        try
        {
            File.Delete(AdminStatePath);
        }
        catch (IOException)
        {
            // A leftover state file is only a stale temp file; never fail the run over it.
        }
    }

    private void StartApp()
    {
        var root = FindRepoRoot();
        _testConnection = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
            ?? "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True")
        { InitialCatalog = $"D3Parking_E2E_{Guid.NewGuid():N}" }.ConnectionString;
        IsolatedSqlConnection = _testConnection;
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"run --project src/D3Parking.Web/D3Parking.Web.csproj -c Release --artifacts-path artifacts/e2e-host --no-launch-profile --urls {BaseUrl}",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__SqlServer"] = _testConnection;
        start.Environment["Account__BaseUrl"] = BaseUrl;
        _app = Process.Start(start) ?? throw new InvalidOperationException("Failed to start the web app.");
    }

    private static async Task<bool> IsUpAsync()
    {
        try
        {
            var response = await Http.GetAsync($"{BaseUrl}/health/ready");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitUntilUpAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsUpAsync())
            {
                return;
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException($"The web app did not become ready at {BaseUrl} within {timeout}.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "D3Soft.D3Parking.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the source root (D3Soft.D3Parking.slnx).");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
