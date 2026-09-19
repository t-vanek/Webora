using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using static Microsoft.Playwright.Assertions;

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

    /// <summary>Creates its own spot through the admin UI and finds it regardless of paging.</summary>
    public static async Task<string> CreateSpotAsync(IPage page)
    {
        var code = $"UI-{Guid.NewGuid():N}"[..20];
        await GotoInteractiveAsync(page, "/admin/parking/spots");
        await page.GetByRole(AriaRole.Button, new() { Name = "Přidat místa", Exact = true }).ClickAsync();
        var dialog = page.Locator(".spots-create-dialog");
        await dialog.Locator("fluent-text-field#single-code input").FillAsync(code);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Přidat", Exact = true }).ClickAsync();
        await Expect(dialog).ToContainTextAsync($"Místo {code} bylo vytvořeno.");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Zavřít", Exact = true }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await SearchSpotsAsync(page, code, 1);
        return code;
    }

    /// <summary>Waits for the filtered server result before acting on a row already on screen.</summary>
    public static async Task SearchSpotsAsync(IPage page, string code, int expectedCount)
    {
        var search = page.Locator("fluent-search#spot-search input");
        await search.FillAsync(code);
        await search.BlurAsync();
        await Expect(page.Locator(".spots-results-head > span"))
            .ToHaveTextAsync($"Nalezeno míst: {expectedCount}");
        await Expect(page.Locator(".spots-desktop-list tbody tr")).ToHaveCountAsync(expectedCount);
        await Expect(page.Locator(".spots-desktop-list tr", new() { HasText = code }).First).ToBeVisibleAsync();
    }

    /// <summary>
    /// Navigates and waits for the InteractiveServer circuit to attach before returning: the
    /// browser must acknowledge applying a render batch. Counting two incoming frames also
    /// counted root-attachment messages and could return while the DOM was still prerendered.
    /// Individual tests additionally wait for the page's data-dependent controls/results.
    /// </summary>
    public static async Task GotoInteractiveAsync(IPage page, string url)
    {
        var attached = new TaskCompletionSource();
        var sockets = 0;
        var received = 0;
        var scriptErrors = 0;
        var failures = new List<string>();
        void OnError(object? sender, string error) => scriptErrors++;
        void OnFailure(object? sender, IRequest request)
        {
            if (request.ResourceType == "script") failures.Add(new Uri(request.Url).AbsolutePath);
        }
        page.PageError += OnError;
        page.RequestFailed += OnFailure;

        void OnWebSocket(object? _, IWebSocket socket)
        {
            if (!socket.Url.Contains("_blazor", StringComparison.Ordinal))
            {
                return;
            }

            socket.FrameSent += (_, frame) =>
            {
                var payload = frame.Text ?? System.Text.Encoding.UTF8.GetString(frame.Binary ?? []);
                if (payload.Contains("OnRenderCompleted", StringComparison.Ordinal))
                {
                    attached.TrySetResult();
                }
            };
        }

        page.WebSocket += OnWebSocket;
        try
        {
            var response = await page.GotoAsync(url);
            try
            {
                await attached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException exception)
            {
                // Paths/status only: never include cookies, input values or URL query strings.
                throw new TimeoutException($"Interactive circuit did not attach. HTTP {response?.Status}; " +
                    $"path {new Uri(page.Url).AbsolutePath}; login form: {await page.Locator("input[name='Input.Password']").CountAsync()}; " +
                    $"error banner visible: {await page.Locator("#blazor-error-ui").IsVisibleAsync()}; sockets {sockets}, frames {received}, JS errors {scriptErrors}, failed scripts {string.Join(", ", failures)}.", exception);
            }
        }
        finally
        {
            page.WebSocket -= OnWebSocket;
            page.PageError -= OnError;
            page.RequestFailed -= OnFailure;
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
