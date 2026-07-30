using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class AdminTests : AdminTest
{
    [Test]
    public async Task Users_list_shows_the_header_chip_panel_and_create_action()
    {
        await Page.GotoAsync("/admin/users");
        await Expect(Page.Locator(".count-chip")).ToBeVisibleAsync();
        await Expect(Page.Locator(".admin-panel")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Nový účet") })).ToBeVisibleAsync();
        await Expect(Page.Locator(".admin-panel").GetByText("admin@d3parking.local")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Roles_list_renders_the_seeded_roles()
    {
        await Page.GotoAsync("/admin/roles");
        await Expect(Page.Locator(".count-chip")).ToBeVisibleAsync();
        await Expect(Page.Locator(".admin-panel")).ToContainTextAsync("Administrator");
    }

    [Test]
    public async Task Parking_spots_list_shows_type_and_state_pills()
    {
        await Page.GotoAsync("/admin/parking/spots");
        await Expect(Page.Locator(".type-pill").First).ToBeVisibleAsync();
        await Expect(Page.Locator(".state-pill").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Spot_generator_creates_a_series_and_marks_it_duplicate_afterwards()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/spots");

        // Unique section prefix per run — the database persists between test runs.
        var prefix = $"G{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000}";

        // Even with the circuit attached, the grid re-renders as it grows and a tab clicked
        // mid-render can flip back, taking the panel (and its inputs) away again. Retry the
        // whole tab + fill sequence until the live preview shows exactly the expected count —
        // visibility alone is not enough, because one of the two fills can be lost while the
        // other lands. The preview assertion doubles as the wait, so there is no blind sleep.
        var countChip = Page.Locator(".batch-preview .count-chip");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await Page.Locator("fluent-tab", new() { HasText = "Generátor řady" }).ClickAsync(new() { Timeout = 5000 });
                await Page.Locator("fluent-text-field#ser-sections input").FillAsync(prefix, new() { Timeout = 5000 });
                await Page.Locator("fluent-number-field#ser-to input").FillAsync("5", new() { Timeout = 5000 });
                await Expect(countChip).ToHaveTextAsync("5", new() { Timeout = 3000 });
                break;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // The panel flipped away or a fill was lost mid-attempt — go around again.
                // (Click/fill timeouts surface as System.TimeoutException, which does NOT derive
                // from PlaywrightException — catching only the latter made this retry loop inert
                // and the spec flaky.)
            }
        }

        // The live preview proves the plan landed (and the create button is enabled).
        await Expect(countChip).ToHaveTextAsync("5");
        await Page.Locator("#ser-create").ClickAsync();

        await Expect(Page.GetByText(new Regex("Vytvořeno míst: 5|Created 5"))).ToBeVisibleAsync();
        await Expect(Page.Locator(".admin-panel")).ToContainTextAsync($"{prefix}-1");

        // Idempotence: the refreshed plan reports the whole series as already existing.
        await Expect(Page.GetByText(new Regex(@"Už existuje \(5\)|Already exists \(5\)"))).ToBeVisibleAsync();
        await Expect(Page.Locator(".batch-preview .count-chip")).ToHaveTextAsync("0");
    }

    [Test]
    public async Task Spot_type_changes_inline_from_the_grid()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/spots");

        // Create a throwaway spot to retype. Even attached, a grid re-render can still swallow
        // an early click — retry until the new row appears, using the row itself as the wait.
        // FluentDataGrid renders a native table, so rows are plain <tr> elements.
        var code = $"T{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000}";
        var row = Page.Locator(".admin-panel tr", new() { HasText = code });
        for (var attempt = 0; attempt < 4 && !await row.IsVisibleAsync(); attempt++)
        {
            await Page.Locator("fluent-text-field#single-code input").FillAsync(code);
            await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Přidat|Add)$") }).ClickAsync();
            try
            {
                await row.WaitForAsync(new() { Timeout = 3000 });
            }
            catch (TimeoutException)
            {
                // The click was lost — go around again.
            }
        }

        await Expect(row).ToBeVisibleAsync();

        // Flip the type via the inline select in the type column (first select of the row).
        // Options carry localized labels; the CSS modifier class stays enum-based.
        await row.Locator("fluent-select").First.ClickAsync();
        await row.Locator("fluent-option", new() { HasText = "Pro držitele ZTP" }).ClickAsync();
        await Expect(row.Locator(".type-pill--Disabled")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Fleet_page_creates_a_vehicle_and_lists_it()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/fleet");
        await Expect(Page.Locator(".form-panel")).ToBeVisibleAsync();
        await Expect(Page.Locator("a[href='admin/parking/fleet']")).ToBeVisibleAsync();

        // Unique plate per run — the database persists between test runs. Same retry idiom as
        // the spots grid: a click landing mid-render can be swallowed, the row is the wait.
        var plate = $"E2E{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000}";
        var row = Page.Locator(".admin-panel tr", new() { HasText = plate });
        for (var attempt = 0; attempt < 4 && !await row.IsVisibleAsync(); attempt++)
        {
            await Page.Locator("fluent-text-field#fleet-plate input").FillAsync(plate);
            await Page.Locator("#fleet-add").ClickAsync();
            try
            {
                await row.WaitForAsync(new() { Timeout = 3000 });
            }
            catch (TimeoutException)
            {
                // The click was lost — go around again.
            }
        }

        await Expect(row).ToBeVisibleAsync();
        // No driver email was filled in, so the funnel column reads "manual pairing only".
        await Expect(row.Locator(".state-pill")).ToHaveTextAsync("Jen ruční párování");
    }

    [Test]
    public async Task Rules_and_pricing_tabs_switch_content()
    {
        // A click that lands during the circuit handshake is silently dropped and the tab never
        // switches — wait for the circuit instead of guessing with a sleep.
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/settings");
        await Expect(Page.GetByText("Ekonomika rezervací")).ToBeVisibleAsync();
        await Page.Locator("fluent-tab", new() { HasText = "Důvěra a ochrana" }).ClickAsync();
        await Expect(Page.GetByText("Graf důvěry", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Saving_the_rules_and_pricing_form_confirms_success()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/settings");
        await Expect(Page.GetByText("Ekonomika rezervací")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Uložit nastavení") }).ClickAsync();
        await Expect(Page.GetByText(new Regex("uloženo|saved", RegexOptions.IgnoreCase))).ToBeVisibleAsync();
    }

    [Test]
    public async Task Collusion_review_renders_its_content()
    {
        // The shared database persists between runs, so flags may legitimately exist: assert the
        // page renders one of its two states (empty state or the flag grid), not emptiness.
        await Page.GotoAsync("/admin/parking/collusion");
        await Expect(Page.Locator(".empty-state").Or(Page.Locator(".admin-panel")).First).ToBeVisibleAsync();
    }
}
