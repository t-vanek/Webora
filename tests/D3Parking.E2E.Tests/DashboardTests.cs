using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class DashboardTests : AdminTest
{
    [Test]
    public async Task Home_shows_the_hero_and_quick_action_tiles()
    {
        await Page.GotoAsync("/");
        // The hero shows the site name, which is admin-editable at /admin/settings — assert the
        // structure (a non-empty title), not the configurable value.
        await Expect(Page.Locator(".home-hero__title")).Not.ToBeEmptyAsync();
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

    [Test]
    public async Task Sidebar_collapses_to_the_rail_and_expands_back()
    {
        // The nav is an interactive island; wait for the circuit before clicking.
        await Pages.GotoInteractiveAsync(Page, "/");

        var nav = Page.Locator(".side-nav");
        var expanded = await nav.BoundingBoxAsync();
        Assert.That(expanded!.Width, Is.GreaterThan(180), "Expanded sidebar should be ~220px wide.");

        await Page.Locator(".side-nav__collapse").ClickAsync();
        await Expect(Page.Locator(".side-nav--rail")).ToBeVisibleAsync();
        var rail = await nav.BoundingBoxAsync();
        Assert.That(rail!.Width, Is.LessThan(100), "Rail should be ~76px wide.");

        await Page.Locator(".side-nav__collapse").ClickAsync();
        await Expect(Page.Locator(".side-nav--rail")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Today_section_shows_the_wallet_card_with_credits_and_points()
    {
        // The wallet card is the one "today" card every parking user has (a score row always
        // exists for the seeded admin); the reservation/queue/resident cards are data-dependent.
        await Page.GotoAsync("/");
        var wallet = Page.Locator(".today-card--wallet");
        await Expect(wallet).ToBeVisibleAsync();
        await Expect(wallet).ToContainTextAsync("kreditů");
        await Expect(wallet).ToContainTextAsync("bodů");
    }
}
