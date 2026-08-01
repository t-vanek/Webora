using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

/// <summary>
/// Drives the map editor in a real browser, because the parts that can only break there are the
/// parts that matter: the pointer drag lives in a JS module, the shapes it moves are rendered by
/// Blazor, and the two only meet over data attributes and JSInterop. A unit test cannot tell whether
/// a rectangle dragged on screen ends up in the database.
/// </summary>
[TestFixture]
public class LotMapEditorTests : AdminTest
{
    private static string MapName => "E2E areál " + Guid.NewGuid().ToString("N")[..8];

    [Test]
    public async Task Drawing_a_rectangle_stores_it_and_a_row_repeats_it_with_the_numbering_carried_on()
    {
        await OpenNewMapAsync();

        // Draw: the tool decides what a drag on empty canvas does.
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.15f, 0.2f, 0.3f, 0.45f);

        var shapes = Page.Locator(".map-shape");
        await Expect(shapes).ToHaveCountAsync(1);

        // A fresh rectangle is selected, so the panel that names it is already open.
        await SelectToolAsync("Výběr", "select");
        await shapes.First.ClickAsync();
        await Expect(Page.Locator(".map-split__side")).ToContainTextAsync("Tvar");

        await FillAsync("fluent-text-field#shape-label", "428");
        await Expect(Page.Locator(".map-shape text").First).ToHaveTextAsync("428");

        // The row tool: five stalls total, so four are added, numbered on from the source.
        await FillAsync("fluent-number-field#row-count", "5");
        await Page.Locator("#map-row").ClickAsync();

        await Expect(shapes).ToHaveCountAsync(5);
        // Scoped to the canvas: ".map-shape" is now five elements, and a text assertion over a
        // multi-element locator is a strict-mode violation rather than a search.
        await Expect(Page.Locator(".map-canvas")).ToContainTextAsync("432");

        // Reloading proves the row is in the database rather than only on screen.
        await Pages.GotoInteractiveAsync(Page, Page.Url);
        await Expect(Page.Locator(".map-shape")).ToHaveCountAsync(5);
    }

    [Test]
    public async Task Dragging_a_shape_moves_it_and_the_new_position_survives_a_reload()
    {
        await OpenNewMapAsync();
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.15f, 0.2f, 0.3f, 0.45f);

        var shape = Page.Locator(".map-shape").First;
        await Expect(shape).ToHaveCountAsync(1);
        var before = await XOfAsync(shape);

        await SelectToolAsync("Výběr", "select");
        var box = await shape.BoundingBoxAsync();
        await DragAsync(box!.X + box.Width / 2, box.Y + box.Height / 2, box.X + box.Width / 2 + 140, box.Y + box.Height / 2);

        // The canvas re-reads from the server after a drag, so this is the stored geometry already.
        await Expect(shape).Not.ToHaveAttributeAsync("data-x", before.ToString(CultureInfo.InvariantCulture));
        var after = await XOfAsync(shape);
        Assert.That(after, Is.GreaterThan(before), "Dragging right must increase the stored x.");

        await Pages.GotoInteractiveAsync(Page, Page.Url);
        Assert.That(await XOfAsync(Page.Locator(".map-shape").First), Is.EqualTo(after).Within(0.01));
    }

    [Test]
    public async Task Linking_by_label_binds_the_drawn_stalls_to_the_spots_that_carry_the_same_code()
    {
        var code = "E2E-" + Guid.NewGuid().ToString("N")[..6];

        // A real spot to match against, created through the catalogue the admin actually uses.
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/spots");
        await FillAsync("fluent-text-field#single-code", code);
        await Page.Locator("fluent-button:has-text('Přidat')").First.ClickAsync();
        await Expect(Page.GetByText(code).First).ToBeVisibleAsync();

        await OpenNewMapAsync();
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.15f, 0.2f, 0.3f, 0.45f);
        await SelectToolAsync("Výběr", "select");
        await Page.Locator(".map-shape").First.ClickAsync();
        await FillAsync("fluent-text-field#shape-label", code);
        // The field is debounced — the label reaching the canvas is what says the server has it,
        // and auto-link matches on the stored label, not on what is in the box.
        await Expect(Page.Locator(".map-canvas")).ToContainTextAsync(code);

        // Deselect so the panel shows the map's own tools, then match labels against spot codes.
        await Page.Keyboard.PressAsync("Escape");
        await Page.Locator(".map-canvas").ClickAsync(new() { Position = new() { X = 20, Y = 20 } });
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Napojit podle popisků") }).ClickAsync();

        await Expect(Page.Locator(".batch-preview .count-chip")).ToHaveTextAsync("1");
        await Expect(Page.Locator(".map-shape").First).ToHaveClassAsync(new Regex("map-shape--linked"));
    }

    /// <summary>
    /// Switches tool and waits until the editor module has taken it. The button's own pressed state
    /// only says the server knows; data-tool is written by the module that handles the drag, so a
    /// gesture fired after this cannot be interpreted by the tool that was active before.
    /// </summary>
    private async Task SelectToolAsync(string label, string tool)
    {
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex($"^{label}$") }).ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", tool);
    }

    /// <summary>Creates a map with a unique name and opens its editor with the circuit attached.</summary>
    private async Task OpenNewMapAsync()
    {
        var name = MapName;
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/map");
        // The circuit being up is not the same as the Fluent components being upgraded: until the
        // custom element is defined, typing into the inner input raises an event nothing is
        // listening to, the name never reaches the binding, and the create silently makes nothing.
        await Page.WaitForFunctionAsync("() => customElements.get('fluent-text-field') !== undefined");
        await FillAsync("fluent-text-field#map-name", name);
        await Page.Locator("#map-create").ClickAsync();
        // Surfaces the real reason when the create is refused, instead of a timeout on the link.
        await Expect(Page.Locator("fluent-message-bar")).ToHaveCountAsync(0);

        // Generous: the first map of the run pays for the cold EF query behind the list, on top of
        // whatever the app was still warming up when the circuit attached.
        var link = Page.GetByRole(AriaRole.Link, new() { Name = name });
        await Expect(link).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await link.ClickAsync();
        await Expect(Page.Locator(".map-canvas")).ToBeVisibleAsync();
        // The module attaches on the first render after the canvas exists; without it every drag
        // below would be a no-op and the failure would read as "nothing was drawn".
        // data-tool is written by the module on attach: the canvas existing is not the same as the
        // module being wired to it, and a drag before that is silently a no-op.
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("data-tool", "select");
    }

    /// <summary>
    /// Drags inside the canvas, addressed as fractions of it. Viewport coordinates would land in the
    /// sidebar or the toolbar depending on the window, and the failure would read as "nothing was
    /// drawn" rather than "the drag missed".
    /// </summary>
    private async Task DrawOnCanvasAsync(float fromX, float fromY, float toX, float toY)
    {
        var box = await Page.Locator(".map-canvas").BoundingBoxAsync();
        await DragAsync(
            box!.X + (box.Width * fromX), box.Y + (box.Height * fromY),
            box.X + (box.Width * toX), box.Y + (box.Height * toY));
    }

    /// <summary>
    /// A pointer drag in three steps. Playwright's DragTo works on elements; here the gesture is the
    /// input — the editor listens for pointerdown/move/up on the canvas and there is nothing to drag
    /// onto. The intermediate move matters: a down-up pair with no move in between is a click.
    /// </summary>
    private async Task DragAsync(float fromX, float fromY, float toX, float toY)
    {
        await Page.Mouse.MoveAsync(fromX, fromY);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync((fromX + toX) / 2, (fromY + toY) / 2);
        await Page.Mouse.MoveAsync(toX, toY);
        await Page.Mouse.UpAsync();
    }

    private static async Task<double> XOfAsync(ILocator shape) =>
        double.Parse(await shape.GetAttributeAsync("data-x") ?? "0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Fills a Fluent field addressed by its id. The real input sits inside the web component's
    /// shadow boundary, so the value goes on the inner input — the same hop the other admin specs
    /// make. The fields here are Immediate, so binding follows the input event on its own.
    /// </summary>
    private Task FillAsync(string selector, string value) =>
        Page.Locator($"{selector} input").FillAsync(value);
}
