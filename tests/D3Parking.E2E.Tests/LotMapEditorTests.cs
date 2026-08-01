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

    [Test]
    public async Task Deleting_with_the_keyboard_and_undoing_it_brings_the_row_back()
    {
        await OpenNewMapAsync();
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.15f, 0.2f, 0.25f, 0.4f);

        var shapes = Page.Locator(".map-shape");
        await Expect(shapes).ToHaveCountAsync(1);

        await SelectToolAsync("Výběr", "select");
        await shapes.First.ClickAsync();
        await FillAsync("fluent-text-field#shape-label", "500");
        await Expect(Page.Locator(".map-canvas")).ToContainTextAsync("500");
        await FillAsync("fluent-number-field#row-count", "4");
        await Page.Locator("#map-row").ClickAsync();
        await Expect(shapes).ToHaveCountAsync(4);

        // Select the lot and delete it with the keyboard. Clicking a shape has to focus the canvas
        // for this to reach the module at all — the pointer handler calls preventDefault, which
        // suppresses the default focus, so it focuses by hand.
        await shapes.First.ClickAsync();
        await Page.Keyboard.PressAsync("Control+a");
        await Page.Keyboard.PressAsync("Delete");
        await Expect(shapes).ToHaveCountAsync(0);

        // Undo puts the whole row back — geometry, labels and all.
        await Page.Locator("#map-undo").ClickAsync();
        await Expect(shapes).ToHaveCountAsync(4);
        await Expect(Page.Locator(".map-canvas")).ToContainTextAsync("503");

        // And it is in the database, not only on screen.
        await Pages.GotoInteractiveAsync(Page, Page.Url);
        await Expect(Page.Locator(".map-shape")).ToHaveCountAsync(4);
    }

    [Test]
    public async Task A_shape_cannot_be_dragged_off_the_canvas_and_lost()
    {
        await OpenNewMapAsync();
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.7f, 0.7f, 0.8f, 0.85f);

        var shape = Page.Locator(".map-shape").First;
        await Expect(shape).ToHaveCountAsync(1);

        // Drag hard past the bottom-right corner. The server clamps and the canvas is corrected to
        // what was stored, so the rectangle is still on the map and still selectable.
        await SelectToolAsync("Výběr", "select");
        var box = await shape.BoundingBoxAsync();
        var canvas = await Page.Locator(".map-canvas").BoundingBoxAsync();
        await DragAsync(
            box!.X + (box.Width / 2), box.Y + (box.Height / 2),
            canvas!.X + canvas.Width + 300, canvas.Y + canvas.Height + 300);

        // Waits rather than reads: the browser commits the dragged position locally and the server's
        // clamped answer arrives a round trip later. The invariant is what is asserted — the shape
        // lies wholly within the map's 1600×900 coordinate space — not one particular pixel.
        await Expect(shape).ToHaveCountAsync(1);
        await Page.WaitForFunctionAsync(
            """
            () => {
                const g = document.querySelector('.map-shape');
                if (!g) return false;
                const n = (k) => parseFloat(g.dataset[k]);
                return n('x') >= 0 && n('y') >= 0
                    && n('x') + n('w') <= 1600.5 && n('y') + n('h') <= 900.5;
            }
            """);

        Assert.That(await XOfAsync(shape), Is.LessThanOrEqualTo(1600),
            "A shape dragged past the edge must stay on the map, or it can never be clicked again.");
    }

    [Test]
    public async Task Clicking_in_draw_mode_stamps_a_rectangle_of_the_last_size_used()
    {
        await OpenNewMapAsync();
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.1f, 0.1f, 0.2f, 0.3f);

        var shapes = Page.Locator(".map-shape");
        await Expect(shapes).ToHaveCountAsync(1);
        var width = await AttrAsync(shapes.First, "data-w");
        var height = await AttrAsync(shapes.First, "data-h");

        // A plain click, no drag: on a plan where every stall is the same size, this is the
        // difference between clicking the row out and redrawing every rectangle by hand.
        var canvas = await Page.Locator(".map-canvas").BoundingBoxAsync();
        await Page.Mouse.ClickAsync(canvas!.X + (canvas.Width * 0.6f), canvas.Y + (canvas.Height * 0.6f));

        await Expect(shapes).ToHaveCountAsync(2);
        Assert.Multiple(async () =>
        {
            Assert.That(await AttrAsync(shapes.Nth(1), "data-w"), Is.EqualTo(width));
            Assert.That(await AttrAsync(shapes.Nth(1), "data-h"), Is.EqualTo(height));
        });
    }

    [Test]
    public async Task Duplicating_with_the_keyboard_copies_the_selection_clear_of_it()
    {
        await OpenNewMapAsync();
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.15f, 0.2f, 0.3f, 0.45f);

        var shapes = Page.Locator(".map-shape");
        await Expect(shapes).ToHaveCountAsync(1);

        await SelectToolAsync("Výběr", "select");
        await shapes.First.ClickAsync();
        await Page.Keyboard.PressAsync("Control+d");

        await Expect(shapes).ToHaveCountAsync(2);
        var first = await XOfAsync(shapes.First);
        var second = await XOfAsync(shapes.Nth(1));
        Assert.That(first, Is.Not.EqualTo(second),
            "A copy exactly on top of the original means the next click grabs the wrong one.");
    }

    [Test]
    public async Task Renumbering_a_selected_row_counts_along_it()
    {
        await OpenNewMapAsync();
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.1f, 0.2f, 0.18f, 0.4f);

        var shapes = Page.Locator(".map-shape");
        await SelectToolAsync("Výběr", "select");
        await shapes.First.ClickAsync();
        await FillAsync("fluent-text-field#shape-label", "1");
        await Expect(Page.Locator(".map-canvas")).ToContainTextAsync("1");
        await FillAsync("fluent-number-field#row-count", "4");
        await Page.Locator("#map-row").ClickAsync();
        await Expect(shapes).ToHaveCountAsync(4);

        // Numbered from one and it should have been 428: the whole row, retyped by hand, is what
        // this replaces.
        await shapes.First.ClickAsync();
        await Page.Keyboard.PressAsync("Control+a");
        await FillAsync("fluent-text-field#renumber-from", "428");
        await Page.Locator("#map-renumber").ClickAsync();

        var canvas = Page.Locator(".map-canvas");
        await Expect(canvas).ToContainTextAsync("428");
        await Expect(canvas).ToContainTextAsync("431");
    }

    [Test]
    public async Task Matching_the_map_to_the_underlay_takes_the_scans_own_proportions()
    {
        await OpenNewMapAsync();
        // The map starts 1600×900; this plan is 2.2:1, which is the case that traces distorted.
        await Page.Locator("#map-background-file").SetInputFilesAsync(new FilePayload
        {
            Name = "plan.png",
            MimeType = "image/png",
            Buffer = GrayPng(220, 100),
        });

        await Expect(Page.Locator("#map-match")).ToBeVisibleAsync();
        await Page.Locator("#map-match").ClickAsync();

        // The natural size is read in the browser and handed to the server, so this proves the whole
        // chain: upload accepted by content sniffing, served back, decoded, measured, map resized.
        await Expect(Page.Locator(".map-canvas")).ToHaveAttributeAsync("viewBox", "0 0 220 100");
    }

    [Test]
    public async Task Finding_a_label_selects_it_and_brings_the_view_onto_it()
    {
        await OpenNewMapAsync();
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.05f, 0.2f, 0.12f, 0.4f);

        var shapes = Page.Locator(".map-shape");
        await SelectToolAsync("Výběr", "select");
        await shapes.First.ClickAsync();
        await FillAsync("fluent-text-field#shape-label", "601");
        await Expect(Page.Locator(".map-canvas")).ToContainTextAsync("601");
        await FillAsync("fluent-number-field#row-count", "6");
        await Page.Locator("#map-row").ClickAsync();
        await Expect(shapes).ToHaveCountAsync(6);

        var wholeMap = await Page.Locator(".map-canvas").GetAttributeAsync("viewBox");
        await FillAsync("fluent-search#map-find", "605");

        // Exactly the one asked for, and the view moved onto it — the point being that on a real
        // plan the shape would be one of five hundred and nowhere near the middle.
        await Expect(Page.Locator(".map-shape.is-selected")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".map-canvas")).Not.ToHaveAttributeAsync("viewBox", wholeMap!);
    }

    [Test]
    public async Task Aligning_a_ragged_pair_lines_them_up_and_undo_puts_them_back()
    {
        await OpenNewMapAsync();
        await SelectToolAsync("Kreslit", "draw");
        await DrawOnCanvasAsync(0.1f, 0.2f, 0.2f, 0.35f);
        await DrawOnCanvasAsync(0.3f, 0.5f, 0.4f, 0.65f);

        var shapes = Page.Locator(".map-shape");
        await Expect(shapes).ToHaveCountAsync(2);

        await SelectToolAsync("Výběr", "select");
        await shapes.First.ClickAsync();
        await Page.Keyboard.PressAsync("Control+a");
        await Expect(Page.Locator(".map-shape.is-selected")).ToHaveCountAsync(2);

        // DOM order is by shape kind, not by position, so neither node is reliably the upper one —
        // the assertion is about the pair, not about which is which.
        var top = Math.Min(await YOfAsync(shapes.First), await YOfAsync(shapes.Nth(1)));
        var lower = Math.Max(await YOfAsync(shapes.First), await YOfAsync(shapes.Nth(1)));
        Assert.That(lower, Is.GreaterThan(top), "The two were drawn ragged on purpose.");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Zarovnat nahoru" }).ClickAsync();

        // Both sit on the topmost edge. Aligning goes down the same path a drag does, so the values
        // being here at all also proves they were stored rather than only previewed.
        var expected = top.ToString(CultureInfo.InvariantCulture);
        await Expect(shapes.First).ToHaveAttributeAsync("data-y", expected);
        await Expect(shapes.Nth(1)).ToHaveAttributeAsync("data-y", expected);

        // …and it picks up the undo step that path records, so the ragged pair comes back.
        await Page.Locator("#map-undo").ClickAsync();
        await Expect(Page.Locator($".map-shape[data-y='{lower.ToString(CultureInfo.InvariantCulture)}']")).ToHaveCountAsync(1);
    }

    private static async Task<double> YOfAsync(ILocator shape) =>
        double.Parse(await shape.GetAttributeAsync("data-y") ?? "0", CultureInfo.InvariantCulture);

    [Test]
    public async Task Importing_an_exported_plan_lays_its_stalls_down_with_their_numbers()
    {
        await OpenNewMapAsync();

        // What a PDF converted to SVG actually looks like, in every awkward detail: the whole row is
        // one path with a subpath per stall, and each number is positioned by its own matrix with no
        // x or y anywhere. Written the tidy way this test proves far less than it appears to — read
        // one subpath per path and it still finds a stall; require x on text and it still finds a
        // number. This is the whole point of the importer, so it is fed the real thing.
        var row = string.Concat(Enumerable.Range(0, 6).Select(i => string.Format(
            CultureInfo.InvariantCulture, "M {0} 0 L {1} 0 L {1} 30 L {0} 30 Z ", i * 50, (i * 50) + 40)));
        var numbers = string.Concat(Enumerable.Range(0, 6).Select(i => string.Format(
            CultureInfo.InvariantCulture,
            @"<text transform=""translate({0} 18)"">{1}</text>", (i * 50) + 20, 700 + i)));
        var svg = $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 1000 500"">
            <g transform=""translate(100 200)""><path d=""{row}"" />{numbers}</g></svg>";

        await Page.Locator("#map-svg-file").SetInputFilesAsync(new FilePayload
        {
            Name = "plan.svg",
            MimeType = "image/svg+xml",
            Buffer = System.Text.Encoding.UTF8.GetBytes(svg),
        });

        await Expect(Page.Locator(".map-shape")).ToHaveCountAsync(6);
        var canvas = Page.Locator(".map-canvas");
        await Expect(canvas).ToContainTextAsync("700");
        await Expect(canvas).ToContainTextAsync("705");
        // The map takes the drawing's own proportions, so 1000×500 arrives as a 2:1 coordinate space.
        await Expect(canvas).ToHaveAttributeAsync("viewBox", "0 0 1000 500");

        // And the lot of it is one step back, because finding out an import landed wrong should not
        // mean deleting hundreds of rectangles by hand.
        await Page.Locator("#map-undo").ClickAsync();
        await Expect(Page.Locator(".map-shape")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// A valid 8-bit greyscale PNG of the given size. Hand-built because the upload is accepted on
    /// its magic bytes and the browser has to genuinely decode it to report a natural size — a stub
    /// would pass neither check.
    /// </summary>
    private static byte[] GrayPng(int width, int height)
    {
        var raw = new byte[height * (width + 1)];
        for (var row = 0; row < height; row++)
        {
            // Leading filter byte per scanline (0 = none); the pixels stay black.
            raw[row * (width + 1)] = 0;
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 0;  // colour type: greyscale
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var payload = new byte[type.Length + data.Length];
        type.CopyTo(payload);
        data.CopyTo(payload.AsSpan(type.Length));
        stream.Write(payload);

        Span<byte> crc = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(payload));
        stream.Write(crc);
    }

    /// <summary>CRC-32/ISO-HDLC, which is what a PNG chunk carries.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
            }
        }

        return ~crc;
    }

    private static async Task<string?> AttrAsync(ILocator locator, string name) => await locator.GetAttributeAsync(name);

    /// <summary>
    /// Switches tool and waits until the editor module has taken it.    /// <summary>
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

        // Either the map shows up in the list or the page says why not. Racing the two turns a
        // refused create from a thirty-second mystery timeout into the message that explains it —
        // which is how a cold circuit dropping the typed name was diagnosed in the first place.
        // The timeout is generous: the first map of a run pays for the cold EF query behind the list.
        var link = Page.GetByRole(AriaRole.Link, new() { Name = name });
        // FluentMessageBar renders a div with this class, not a <fluent-message-bar> element; the
        // tag name never matched, so this diagnostic quietly did nothing until it was checked.
        var complaint = Page.Locator(".fluent-messagebar-message");
        var appeared = await Task.WhenAny(
            link.WaitForAsync(new() { Timeout = 30_000 }),
            complaint.WaitForAsync(new() { Timeout = 30_000 }));

        if (await complaint.CountAsync() > 0)
        {
            Assert.Fail($"Creating the map was refused: {await complaint.First.InnerTextAsync()}");
        }

        await appeared;
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
