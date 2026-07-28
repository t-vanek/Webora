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
    /// <summary>Fluent text fields wrap a real input in their shadow DOM.</summary>
    public static ILocator FluentField(IPage page, string name) =>
        page.Locator($"fluent-text-field[name='{name}']").Locator("input");

    public static Task SubmitAsync(IPage page) =>
        page.Locator("fluent-button[type=submit], button[type=submit]").First.ClickAsync();

    public static async Task LoginAsync(IPage page, string email = Admin.Email, string password = Admin.Password)
    {
        await page.GotoAsync("/login");
        await FluentField(page, "Input.Email").FillAsync(email);
        await FluentField(page, "Input.Password").FillAsync(password);
        await SubmitAsync(page);
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
