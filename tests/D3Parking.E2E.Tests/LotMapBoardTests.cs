using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

/// <summary>
/// The whole chain the map engine exists for, driven end to end: trace a stall, turn it into a real
/// spot, publish the drawing, and find it on the manager's board coloured by what that spot is doing
/// today. Every link in it lives in a different layer, so nothing short of this proves it joins up.
///
/// The other half of the switch's condition — no drawing published, so no map view offered — is not
/// covered here on purpose. It depends on there being no published map anywhere, which is global
/// state this suite shares with every other spec; a test that has to unpublish whatever it finds
/// first would fail for reasons that have nothing to do with the behaviour it names.
/// </summary>
[TestFixture]
public class LotMapBoardTests : AdminTest
{
    [Test]
    public async Task A_published_map_appears_on_the_board_and_clicking_a_stall_opens_its_detail()
    {
        var code = "MAP-" + Guid.NewGuid().ToString("N")[..6];
        var name = "E2E deska " + Guid.NewGuid().ToString("N")[..8];

        // --- trace one stall and make it a real spot ---
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/map");
        await Page.WaitForFunctionAsync("() => customElements.get('fluent-text-field') !== undefined");
        await Page.Locator("fluent-text-field#map-name input").FillAsync(name);
        await Page.Locator("#map-create").ClickAsync();

        var link = Page.GetByRole(AriaRole.Link, new() { Name = name });
        await Expect(link).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await link.ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "select");

        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Kreslit$") }).ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "draw");
        var canvas = await Page.Locator(".map-canvas").BoundingBoxAsync();
        await Page.Mouse.MoveAsync(canvas!.X + (canvas.Width * 0.3f), canvas.Y + (canvas.Height * 0.3f));
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(canvas.X + (canvas.Width * 0.4f), canvas.Y + (canvas.Height * 0.45f));
        await Page.Mouse.MoveAsync(canvas.X + (canvas.Width * 0.45f), canvas.Y + (canvas.Height * 0.5f));
        await Page.Mouse.UpAsync();

        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Výběr$") }).ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "select");
        await Page.Locator(".map-shape").First.ClickAsync();
        await Page.Locator("fluent-text-field#shape-label input").FillAsync(code);
        await Expect(Page.Locator(".map-canvas")).ToContainTextAsync(code);

        // A second stall well clear of the first. Creating spots lives in the multi-selection panel,
        // so there has to be more than one shape — and two that do not overlap keep the clicks below
        // unambiguous.
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Kreslit$") }).ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "draw");
        await Page.Mouse.MoveAsync(canvas.X + (canvas.Width * 0.6f), canvas.Y + (canvas.Height * 0.3f));
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(canvas.X + (canvas.Width * 0.68f), canvas.Y + (canvas.Height * 0.42f));
        await Page.Mouse.MoveAsync(canvas.X + (canvas.Width * 0.72f), canvas.Y + (canvas.Height * 0.5f));
        await Page.Mouse.UpAsync();
        await Expect(Page.Locator(".map-shape")).ToHaveCountAsync(2);

        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Výběr$") }).ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "select");
        await Page.Locator(".map-shape").First.ClickAsync();
        await Page.Keyboard.PressAsync("Control+a");

        // Waits on the panel the server rendered, not on the classes JS toggles. The two disagree
        // for the length of one round trip — that gap is what makes dragging fast — so the classes
        // say "selected" before the component that owns the panel has heard about it.
        await Expect(Page.Locator("#map-create-spots")).ToBeVisibleAsync();
        await Page.Locator("#map-create-spots").ClickAsync();
        // FluentMessageBar renders a plain div, not a <fluent-message-bar> element — the rest of the
        // suite asserts its content by text for the same reason.
        await Expect(Page.Locator(".fluent-messagebar-message")).ToContainTextAsync("Založeno");

        // --- publish it ---
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Publikovat$") }).ClickAsync();
        await Expect(Page.GetByText("Publikováno").First).ToBeVisibleAsync();

        // --- and it is on the board, coloured by what the spot is doing ---
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/dashboard");
        await Page.Locator("#lot-view-map").ClickAsync();

        var stall = Page.Locator(".map-view__shape--spot").First;
        await Expect(stall).ToBeVisibleAsync();
        // Nothing is booked on a spot created seconds ago, so the board says free.
        await Expect(stall).ToHaveClassAsync(new Regex("map-view__state--Free"));

        // Clicking it opens the same side panel a tile does — that is the point of sharing the
        // selection rather than giving the map a detail view of its own.
        await stall.ClickAsync();
        await Expect(Page.Locator(".lot-split__side")).ToContainTextAsync(code);
    }
}
