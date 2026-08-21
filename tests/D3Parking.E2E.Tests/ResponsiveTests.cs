using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class ResponsiveTests : PageTest
{
    public override BrowserNewContextOptions ContextOptions() => new()
    {
        BaseURL = WebAppFixture.BaseUrl,
        Locale = "cs-CZ",
        StorageStatePath = WebAppFixture.AdminStatePath,
        ViewportSize = new() { Width = 390, Height = 800 },
    };

    private async Task<int> HorizontalOverflowAsync() =>
        await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");

    [Test]
    public async Task Home_does_not_overflow_horizontally_at_390px()
    {
        await Page.GotoAsync("/");
        Assert.That(await HorizontalOverflowAsync(), Is.LessThanOrEqualTo(1));
    }

    [Test]
    public async Task Wallet_chip_drops_its_credits_label_to_keep_the_header_tools_visible()
    {
        await Page.GotoAsync("/");
        await Expect(Page.Locator(".wallet-chip")).ToBeVisibleAsync();
        await Expect(Page.Locator(".wallet-chip small")).ToBeHiddenAsync();
    }

    [Test]
    public async Task Header_tools_stay_inside_the_viewport()
    {
        // The header overflow this guards was data-dependent: it only appeared once the wallet
        // showed a tier chip and the bell a count, so a plain overflow check on a fresh database
        // sailed past it. Assert the geometry of the tools row itself instead.
        await Page.GotoAsync("/");
        var tools = Page.Locator(".app-header .stack-horizontal").First;
        var box = await tools.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null);
        Assert.That(box!.X + box.Width, Is.LessThanOrEqualTo(390 + 1),
            "The header tools row extends past the 390px viewport.");
    }

    [Test]
    public async Task A_wide_admin_table_scrolls_inside_its_panel_not_the_page()
    {
        await Page.GotoAsync("/admin/users");
        Assert.That(await HorizontalOverflowAsync(), Is.LessThanOrEqualTo(1));
    }

    [Test]
    public async Task Parking_planner_does_not_overflow_horizontally()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/settings");
        Assert.That(await HorizontalOverflowAsync(), Is.LessThanOrEqualTo(1));
        await Expect(Page.GetByRole(AriaRole.Switch, new() { NameString = "Použít týdenní limit" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".planner-summary")).ToHaveCountAsync(0);

        foreach (var (id, label) in new[]
                 {
                     ("operations", "Provoz a přehled"),
                     ("residents", "Rezidenti"),
                     ("location", "Lokalita a limity"),
                     ("notifications", "Notifikace"),
                 })
        {
            // Fluent Tabs moves later tabs into its compact overflow menu. Dispatching the tab's
            // own click selects the same panel while keeping this test focused on panel geometry.
            await Page.Locator($"#{id}").EvaluateAsync("element => element.click()");
            Assert.That(await HorizontalOverflowAsync(), Is.LessThanOrEqualTo(1),
                $"The {label} tab overflows the mobile viewport.");
        }
    }

    [Test]
    public async Task Nav_drawer_opens_from_the_toggle_and_closes_after_navigating()
    {
        // The drawer is an interactive island; wait for the circuit before clicking.
        await Pages.GotoInteractiveAsync(Page, "/");

        await Expect(Page.Locator(".nav-drawer")).ToBeHiddenAsync();
        await Page.Locator(".nav-drawer-toggle").ClickAsync();
        await Expect(Page.Locator(".nav-drawer--open")).ToBeVisibleAsync();

        await Page.Locator(".nav-drawer").GetByRole(AriaRole.Link, new() { NameString = "Můj přínos" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("leaderboard"));
        await Expect(Page.Locator(".nav-drawer--open")).ToHaveCountAsync(0);
    }
}
