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
    public async Task Achievement_or_credit_metric_keeps_space_between_number_and_label()
    {
        await Page.GotoAsync("/");

        var metric = Page.Locator(".today-card--wallet .today-card__value--metric");
        await Expect(metric).ToBeVisibleAsync();
        await Expect(metric.Locator(":scope > span")).ToHaveCountAsync(2);
        var gap = await metric.EvaluateAsync<string>("element => getComputedStyle(element).columnGap");
        Assert.That(gap, Is.Not.EqualTo("0px").And.Not.EqualTo("normal"));
    }

    [Test]
    public async Task A_quick_action_tile_navigates_to_its_page()
    {
        await Page.GotoAsync("/");
        await Page.Locator(".home-tile", new() { HasText = "Ocenění" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("parking/achievements"));
    }

    [Test]
    public async Task Sidebar_gives_both_authorization_catalogs_one_entry_that_stays_lit()
    {
        // Roles and groups are two tabs of one screen, so the menu names it once. The groups tab
        // keeps its own route, and NavLink only matches the href it was given — landing there used
        // to leave the whole menu unlit, which reads as "you navigated off the menu".
        await Pages.GotoInteractiveAsync(Page, "/admin/permission-groups");

        var nav = Page.Locator(".side-nav");
        await Expect(nav).ToContainTextAsync("Správa oprávnění");
        await Expect(nav.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Skupiny oprávnění$") }))
            .ToHaveCountAsync(0);
        await Expect(nav.Locator(".side-nav__item--active")).ToHaveCountAsync(1);
        await Expect(nav.Locator(".side-nav__item--active")).ToContainTextAsync("Správa oprávnění");
    }

    [Test]
    public async Task Sidebar_lights_only_the_parking_screen_you_are_actually_on()
    {
        // /parking is the parent path of both child screens, so NavLink's default prefix matching
        // lit the reservation entry on top of them — two items filled violet, neither one an
        // honest answer to "where am I".
        var children = new[]
        {
            ("/parking/achievements", "Ocenění"),
            ("/parking/reports", "Moje hlášení"),
        };

        foreach (var (url, label) in children)
        {
            await Pages.GotoInteractiveAsync(Page, url);

            var active = Page.Locator(".side-nav .side-nav__item--active");
            await Expect(active).ToHaveCountAsync(1);
            await Expect(active).ToContainTextAsync(label);
        }
    }

    [Test]
    public async Task Sidebar_collapses_to_the_rail_and_expands_back()
    {
        // The nav is an interactive island; wait for the circuit before clicking.
        await Pages.GotoInteractiveAsync(Page, "/");

        var nav = Page.Locator(".side-nav");
        var expanded = await WidthAsync(nav);
        Assert.That(expanded, Is.GreaterThan(180), "Expanded sidebar should be ~220px wide.");

        await Page.Locator(".side-nav__collapse").ClickAsync();
        await Expect(Page.Locator(".side-nav--rail")).ToBeVisibleAsync();
        var rail = await WidthAsync(nav);
        Assert.That(rail, Is.LessThan(100), "Rail should be ~76px wide.");

        await Page.Locator(".side-nav__collapse").ClickAsync();
        await Expect(Page.Locator(".side-nav--rail")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// Reads the rendered width of an element once it is actually on screen. BoundingBoxAsync
    /// does no visibility wait of its own — for an element that is hidden, has no layout box yet
    /// or was just detached by a hydration re-render it returns null rather than retrying, which
    /// is how the sidebar spec used to fail intermittently with a NullReferenceException. An
    /// attached circuit says the island can take clicks, not that it has been laid out, so every
    /// measurement gates on ToBeVisibleAsync first.
    /// </summary>
    private async Task<float> WidthAsync(ILocator locator)
    {
        await Expect(locator).ToBeVisibleAsync();
        var box = await locator.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "Element is visible but reports no bounding box.");
        return box!.Width;
    }

    [Test]
    public async Task Interactive_islands_stay_in_the_negotiated_culture_after_hydration()
    {
        // Regression guard for the split-language page: the static shell renders in the culture
        // the HTTP request negotiated, but the interactive islands render with the culture of the
        // _blazor WebSocket handshake — which carries no Accept-Language. Before the culture
        // cookie was pinned at first load, hydration re-rendered the nav in the server's own
        // culture (English), leaving one page in two languages.
        await Pages.GotoInteractiveAsync(Page, "/");

        // Give the just-attached circuit time to replace the prerendered nav; the wrong-culture
        // re-render arrived within a second of the handshake when this was broken.
        await Task.Delay(1500);

        var nav = Page.Locator(".side-nav");
        await Expect(nav).ToContainTextAsync("Rezervace místa");
        await Expect(nav).Not.ToContainTextAsync("Reserve a spot");
    }

    [Test]
    public async Task Today_section_shows_the_configured_achievement_or_credit_metric()
    {
        // The summary card is the one "today" card every parking user has. Depending on the
        // incentive configuration it shows either credits or achievements.
        await Page.GotoAsync("/");
        var wallet = Page.Locator(".today-card--wallet");
        await Expect(wallet).ToBeVisibleAsync();
        await Expect(wallet).ToHaveAttributeAsync("href", "parking/achievements");
        await Expect(wallet.Locator(".today-card__label")).Not.ToBeEmptyAsync();
        await Expect(wallet.Locator(".today-card__value--metric > span")).ToHaveCountAsync(2);
    }

    [Test]
    public async Task My_reservations_live_on_home_instead_of_the_booking_page()
    {
        await Page.GotoAsync("/");
        var reservations = Page.Locator(".reservations-panel");
        await Expect(reservations).ToBeVisibleAsync();
        await Expect(reservations.GetByText("Moje rezervace", new() { Exact = true })).ToBeVisibleAsync();

        await Pages.GotoInteractiveAsync(Page, "/parking");
        await Expect(Page.Locator(".reservations-panel")).ToHaveCountAsync(0);
    }
}
