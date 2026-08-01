using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

/// <summary>
/// The driver's half of the map: a published plan, a window with something free in it, and a
/// booking made by clicking the stall rather than a card. Drives the same component the manager's
/// board uses, but with the other meaning of a click — which is the whole reason it takes its
/// behaviour as parameters.
/// </summary>
[TestFixture]
public class ReserveMapTests : AdminTest
{
    [Test]
    public async Task Clicking_a_free_stall_on_the_map_books_it()
    {
        var code = "RSV-" + Guid.NewGuid().ToString("N")[..6];
        await PublishMapWithOneStallAsync(code);
        await ReclaimCreditAsync();

        // A driver may hold only one booking over a given window, so the window has to be one nobody
        // — including an earlier run of this suite — has taken. Well past the two-to-nine days and
        // 10:00–14:00 that ParkingTests works in, and drawn from six hundred non-overlapping slots.
        await Pages.GotoInteractiveAsync(Page, "/parking");
        var bar = Page.Locator(".booking-bar__row");
        await Expect(bar.Locator("input[type=date]")).ToBeVisibleAsync();

        var iso = DateTime.UtcNow.Date.AddDays(40 + Random.Shared.Next(150)).ToString("yyyy-MM-dd");
        var hh = (6 + Random.Shared.Next(2)).ToString("D2");
        var mm = Random.Shared.Next(2) == 0 ? "00" : "30";
        var endMm = mm == "00" ? "25" : "55";
        var date = bar.Locator("input[type=date]");
        await date.FillAsync(iso);
        await date.BlurAsync();
        await bar.Locator("input[type=time]").Nth(0).FillAsync($"{hh}:{mm}");
        await bar.Locator("input[type=time]").Nth(0).BlurAsync();
        await bar.Locator("input[type=time]").Nth(1).FillAsync($"{hh}:{endMm}");
        await bar.Locator("input[type=time]").Nth(1).BlurAsync();
        await Expect(date).ToHaveValueAsync(iso);

        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Najít") }).ClickAsync();
        await Expect(Page.Locator(".spot-card").First).ToBeVisibleAsync();

        // Switch to the plan. The list stays available; this only changes how the same results read.
        await Page.Locator("#reserve-view-map").ClickAsync();

        // Located by the label the stall carries rather than by its tooltip: the wording of a
        // tooltip is not the behaviour under test, and pinning it makes the spec break on rephrasing.
        var stall = Page.Locator($".map-view__shape--spot:has-text('{code}')");
        await Expect(stall).ToHaveCountAsync(1);
        await Expect(stall).ToHaveClassAsync(new Regex("map-view__state--Free"));
        // Free means bookable: if this were the non-interactive variant the click below would land
        // on nothing and the failure would read as "no confirmation appeared".
        await Expect(stall).Not.ToHaveClassAsync(new Regex("map-view__shape--idle"));
        await Expect(stall).ToHaveAttributeAsync("role", "button");

        await stall.ClickAsync();

        await Expect(Page.Locator(".fluent-messagebar-message")).ToContainTextAsync("rezervováno");
        // Booking it re-runs the search, so the stall the driver just took is no longer on offer —
        // which is the map reading the new state rather than being told about it.
        await Expect(stall).ToHaveClassAsync(new Regex("map-view__state--Taken"));

        // Give the booking back. Not politeness: every run spends credits from the one seeded
        // account this suite signs in as, and without the refund a timely cancel brings, a handful
        // of runs empty the wallet — after which the map correctly draws every free stall as
        // unaffordable and this spec fails for a reason that has nothing to do with the map.
        // The row's presence is also the proof that the click booked something the driver now holds.
        var booking = Page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(code) });
        await Expect(booking).ToHaveCountAsync(1);
        var undo = booking.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Zrušit$") });
        await undo.ClickAsync();
        // A cancelled booking stays in the history; what it loses is the ability to be cancelled again.
        await Expect(undo).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_stall_that_is_not_free_is_drawn_but_cannot_be_clicked()
    {
        var code = "RSV-" + Guid.NewGuid().ToString("N")[..6];
        await PublishMapWithOneStallAsync(code);

        // Deactivating the spot takes it out of every search without deleting the rectangle that
        // draws it, which is exactly the "ours, but not on offer" case the map has to distinguish
        // from somebody else's stall.
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/spots");
        await Page.Locator("fluent-search#spot-search input").FillAsync(code);
        var row = Page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex(code) });
        await Expect(row).ToHaveCountAsync(1);
        await row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Deaktivovat") }).ClickAsync();

        await Pages.GotoInteractiveAsync(Page, "/parking");
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Najít") }).ClickAsync();
        await Expect(Page.Locator("#reserve-view-map")).ToBeVisibleAsync();
        await Page.Locator("#reserve-view-map").ClickAsync();

        var stall = Page.Locator($".map-view__shape--spot:has-text('{code}')");
        await Expect(stall).ToHaveCountAsync(1);
        await Expect(stall).ToHaveClassAsync(new Regex("map-view__state--Taken"));
        // Coloured, but not a control: no role, no tab stop, nothing a click would do.
        await Expect(stall).ToHaveClassAsync(new Regex("map-view__shape--idle"));
        await Expect(stall).Not.ToHaveAttributeAsync("role", "button");
    }

    /// <summary>
    /// Cancels the seeded account's outstanding bookings so it can afford another one.
    /// </summary>
    /// <remarks>
    /// Every spec in this suite signs in as the same driver, and a booking costs credits from that
    /// one monthly allowance. Nothing hands them back except cancelling in time, so after a handful
    /// of runs the wallet is empty — at which point the map quite correctly draws every free stall
    /// as unaffordable and a spec about clicking one fails for a reason that is not about the map.
    /// Arranging the precondition is the fix; asserting around it would be asserting the wrong thing.
    /// </remarks>
    private async Task ReclaimCreditAsync()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");
        var cancel = Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Zrušit$") });

        // Bounded: a page of bookings at a time, and never an unbounded loop against a live UI.
        for (var i = 0; i < 25 && await cancel.CountAsync() > 0; i++)
        {
            var before = await cancel.CountAsync();
            await cancel.First.ClickAsync();
            await Expect(cancel).ToHaveCountAsync(before - 1);
        }
    }

    /// <summary>
    /// Traces two stalls, turns them into spots, and publishes the drawing — the state the driver's
    /// screen needs before it can offer a map at all.
    /// </summary>
    private async Task PublishMapWithOneStallAsync(string code)
    {
        var name = "E2E rezervace " + Guid.NewGuid().ToString("N")[..8];
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/map");
        await Page.WaitForFunctionAsync("() => customElements.get('fluent-text-field') !== undefined");
        await Page.Locator("fluent-text-field#map-name input").FillAsync(name);
        await Page.Locator("#map-create").ClickAsync();

        var link = Page.GetByRole(AriaRole.Link, new() { Name = name });
        await Expect(link).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await link.ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "select");

        var canvas = await Page.Locator(".map-canvas").BoundingBoxAsync()
            ?? throw new InvalidOperationException("The map canvas has no box to draw in.");
        await DrawAsync(canvas, 0.25f, 0.3f, 0.4f, 0.5f);
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Výběr$") }).ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "select");
        await Page.Locator(".map-shape").First.ClickAsync();
        await Page.Locator("fluent-text-field#shape-label input").FillAsync(code);
        await Expect(Page.Locator(".map-canvas")).ToContainTextAsync(code);

        // A second stall so the panel offers the multi-selection tools, well clear of the first.
        await DrawAsync(canvas, 0.6f, 0.3f, 0.72f, 0.5f);
        await Expect(Page.Locator(".map-shape")).ToHaveCountAsync(2);
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Výběr$") }).ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "select");
        await Page.Locator(".map-shape").First.ClickAsync();
        await Page.Keyboard.PressAsync("Control+a");

        await Expect(Page.Locator("#map-create-spots")).ToBeVisibleAsync();
        await Page.Locator("#map-create-spots").ClickAsync();
        await Expect(Page.Locator(".fluent-messagebar-message")).ToContainTextAsync("Založeno");

        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Publikovat$") }).ClickAsync();
        await Expect(Page.GetByText("Publikováno").First).ToBeVisibleAsync();
    }

    private async Task DrawAsync(LocatorBoundingBoxResult canvas, float x1, float y1, float x2, float y2)
    {
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Kreslit$") }).ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "draw");
        await Page.Mouse.MoveAsync(canvas.X + (canvas.Width * x1), canvas.Y + (canvas.Height * y1));
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(canvas.X + (canvas.Width * (x1 + x2) / 2), canvas.Y + (canvas.Height * (y1 + y2) / 2));
        await Page.Mouse.MoveAsync(canvas.X + (canvas.Width * x2), canvas.Y + (canvas.Height * y2));
        await Page.Mouse.UpAsync();
    }
}
