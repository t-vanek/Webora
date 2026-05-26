using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Webora.E2E.Tests;

[TestFixture]
public class DashboardTests : AdminTest
{
    [Test]
    public async Task Home_shows_the_hero_and_quick_action_tiles()
    {
        await Page.GotoAsync("/");
        await Expect(Page.Locator(".home-hero__title")).ToHaveTextAsync("Webora");
        await Expect(Page.Locator(".home-tile").First).ToBeVisibleAsync();
        Assert.That(await Page.Locator(".home-tile").CountAsync(), Is.GreaterThan(3));
    }

    [Test]
    public async Task A_quick_action_tile_navigates_to_its_page()
    {
        await Page.GotoAsync("/");
        await Page.Locator(".home-tile", new() { HasText = "Žebříček" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("parking/leaderboard"));
    }
}
