using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class ParkingTests : AdminTest
{
    [Test]
    public async Task Leaderboard_shows_the_hero_score_card_and_panels()
    {
        await Page.GotoAsync("/parking/leaderboard");
        await Expect(Page.Locator(".tier-ring")).ToBeVisibleAsync();
        await Expect(Page.Locator(".hero__points")).ToBeVisibleAsync();
        await Expect(Page.Locator(".trust-meter")).ToBeVisibleAsync();
        await Expect(Page.Locator(".admin-panel").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Searching_a_window_shows_a_price_quote()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");
        await Expect(Page.Locator(".booking-bar__row input[type=date]")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Najít") }).ClickAsync();
        await Expect(Page.Locator(".price-tag__num")).ToBeVisibleAsync();
        await Expect(Page.Locator(".quote-meter .meter")).ToBeVisibleAsync();
        await Expect(Page.Locator(".quote-panel .pill")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Reserving_a_future_window_round_trips_the_picked_time()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");
        var bar = Page.Locator(".booking-bar__row");
        await Expect(bar.Locator("input[type=date]")).ToBeVisibleAsync();

        // A few days out (never "in the past") at a two-digit hour, randomised so
        // re-runs don't collide on an already-booked spot.
        var day = DateTime.UtcNow.Date.AddDays(2 + Random.Shared.Next(8));
        var iso = day.ToString("yyyy-MM-dd");
        var hour = 10 + Random.Shared.Next(5); // 10..14
        var hh = hour.ToString("D2");

        var date = bar.Locator("input[type=date]");
        await date.FillAsync(iso);
        await date.BlurAsync();
        await bar.Locator("input[type=time]").Nth(0).FillAsync($"{hh}:00");
        await bar.Locator("input[type=time]").Nth(0).BlurAsync();
        await bar.Locator("input[type=time]").Nth(1).FillAsync($"{hh}:30");
        await bar.Locator("input[type=time]").Nth(1).BlurAsync();
        await Expect(date).ToHaveValueAsync(iso);

        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Najít") }).ClickAsync();
        var firstSpot = Page.Locator(".spot-card").First;
        await Expect(firstSpot).ToBeVisibleAsync();
        await firstSpot.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Rezervovat") }).ClickAsync();

        // The window is stored as the picked wall-clock in UTC, so the list must show exactly the
        // picked time — this guards the timezone regression. Cancelled leftovers from earlier runs
        // can carry the same random slot, so the row is pinned down as the one still holding a
        // Zrušit button: only the fresh Reserved booking has one.
        var csDate = day.ToString("dd.MM.yyyy");
        var cancelButton = Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Zrušit") });
        var row = Page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex($"{Regex.Escape(csDate)} {hour}:00") })
            .Filter(new() { Has = cancelButton });
        await Expect(row).ToBeVisibleAsync();

        // Clean up: cancel what we just booked. This far ahead of the start the charge refunds in
        // full, so repeated runs don't drain the admin wallet until the Reserve button disables
        // (which is exactly how this suite once ground to a halt).
        await row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Zrušit") }).ClickAsync();
        await Expect(row).ToHaveCountAsync(0);
    }
}
