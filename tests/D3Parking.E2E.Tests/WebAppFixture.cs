using System.Diagnostics;
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
        Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:5163";

    // Unique per test process: two suites running side by side (e.g. the parallel verification
    // instance next to the IDE one) must not overwrite each other's saved session.
    public static readonly string AdminStatePath =
        Path.Combine(Path.GetTempPath(), $"d3parking-e2e-admin-state-{Environment.ProcessId}.json");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _app;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        // Reuse the bundled browser if present (PLAYWRIGHT_BROWSERS_PATH); otherwise fetch it.
        Microsoft.Playwright.Program.Main(["install", "chromium"]);

        if (!await IsUpAsync())
        {
            StartApp();
            await WaitUntilUpAsync(TimeSpan.FromMinutes(3));
        }

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
    public void TearDown()
    {
        if (_app is { HasExited: false })
        {
            _app.Kill(entireProcessTree: true);
            _app.Dispose();
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
        _app = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments =
                $"run --project src/D3Parking.Web/D3Parking.Web.csproj -c Debug --urls {BaseUrl}",
            WorkingDirectory = root,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start the web app.");
    }

    private static async Task<bool> IsUpAsync()
    {
        try
        {
            var response = await Http.GetAsync($"{BaseUrl}/login");
            return (int)response.StatusCode < 500;
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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "D3Parking.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (D3Parking.slnx).");
    }
}
