using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace D3Parking.E2E.Tests;

/// <summary>Seeded administrator (IdentitySeed in appsettings.json).</summary>
public static class Admin
{
    public const string Email = "admin@d3parking.local";
    public const string Password = "Admin123$";
}

/// <summary>Shared browser interactions.</summary>
public static class Pages
{
    /// <summary>Form inputs addressed by their posted name — the redesigned auth pages render
    /// native inputs (Blazor InputText), so no shadow DOM hop is needed.</summary>
    public static ILocator Field(IPage page, string name) =>
        page.Locator($"input[name='{name}']");

    public static Task SubmitAsync(IPage page) =>
        page.Locator("fluent-button[type=submit], button[type=submit]").First.ClickAsync();

    public static async Task LoginAsync(IPage page, string email = Admin.Email, string password = Admin.Password)
    {
        await page.GotoAsync("/login");
        await Field(page, "Input.Email").FillAsync(email);
        await Field(page, "Input.Password").FillAsync(password);
        await SubmitAsync(page);
    }

    /// <summary>
    /// Navigates and waits for the InteractiveServer circuit to attach before returning: the
    /// _blazor websocket must open and the server must answer over it (handshake ack + first
    /// render batch). This replaces the fixed "hydration" sleeps — those guessed, and a click
    /// fired during the handshake is silently dropped, which made the suite flaky on slow runs.
    /// SignalR keep-alive pings backstop the wait, so it can never hang past the timeout.
    /// </summary>
    public static async Task GotoInteractiveAsync(IPage page, string url)
    {
        var attached = new TaskCompletionSource();

        void OnWebSocket(object? _, IWebSocket socket)
        {
            if (!socket.Url.Contains("_blazor", StringComparison.Ordinal))
            {
                return;
            }

            var frames = 0;
            socket.FrameReceived += (_, _) =>
            {
                // Frame 1 is the SignalR handshake ack, frame 2 the first render batch — after
                // that the interactive islands have their event handlers wired.
                if (Interlocked.Increment(ref frames) >= 2)
                {
                    attached.TrySetResult();
                }
            };
        }

        page.WebSocket += OnWebSocket;
        try
        {
            await page.GotoAsync(url);
            await attached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            page.WebSocket -= OnWebSocket;
        }
    }
}

/// <summary>Base for specs that run signed out.</summary>
public abstract class AnonymousTest : PageTest
{
    public override BrowserNewContextOptions ContextOptions() => new()
    {
        BaseURL = WebAppFixture.BaseUrl,
        Locale = "cs-CZ",
    };
}

/// <summary>Base for specs that run as the seeded admin.</summary>
public abstract class AdminTest : PageTest
{
    public override BrowserNewContextOptions ContextOptions() => new()
    {
        BaseURL = WebAppFixture.BaseUrl,
        Locale = "cs-CZ",
        StorageStatePath = WebAppFixture.AdminStatePath,
    };
}
