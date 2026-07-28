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
    public async Task A_wide_admin_table_scrolls_inside_its_panel_not_the_page()
    {
        await Page.GotoAsync("/admin/users");
        Assert.That(await HorizontalOverflowAsync(), Is.LessThanOrEqualTo(1));
    }
}
