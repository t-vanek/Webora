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
        await Page.GotoAsync("/admin/parking/spots");
        await Page.WaitForTimeoutAsync(1500); // hydrate the InteractiveServer circuit

        // Unique section prefix per run — the database persists between test runs.
        var prefix = $"G{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000}";

        // Hydration slows down as the grid grows, and interactions racing the handshake are
        // lost: a tab clicked mid-handshake switches only visually and flips back once the
        // circuit binds, taking the panel (and its inputs) away again. Retry the whole
        // tab + fill sequence with short timeouts until the live preview responds.
        var countChip = Page.Locator(".batch-preview .count-chip");
        for (var attempt = 0; attempt < 5 && !await countChip.IsVisibleAsync(); attempt++)
        {
            try
            {
                await Page.Locator("fluent-tab", new() { HasText = "Generátor řady" }).ClickAsync(new() { Timeout = 5000 });
                await Page.Locator("fluent-text-field#ser-sections input").FillAsync(prefix, new() { Timeout = 5000 });
                await Page.Locator("fluent-number-field#ser-to input").FillAsync("5", new() { Timeout = 5000 });
            }
            catch (TimeoutException)
            {
                // The panel flipped away mid-attempt — go around again.
            }

            await Page.WaitForTimeoutAsync(1200);
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
        await Page.GotoAsync("/admin/parking/spots");
        await Page.WaitForTimeoutAsync(1500); // hydrate the InteractiveServer circuit

        // Create a throwaway spot to retype. Circuit hydration slows down as the grid grows,
        // and a click fired during the handshake is silently dropped — retry until the row
        // appears. FluentDataGrid renders a native table, so rows are plain <tr> elements.
        var code = $"T{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000}";
        var row = Page.Locator(".admin-panel tr", new() { HasText = code });
        for (var attempt = 0; attempt < 4 && !await row.IsVisibleAsync(); attempt++)
        {
            await Page.Locator("fluent-text-field#single-code input").FillAsync(code);
            await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Přidat|Add)$") }).ClickAsync();
            await Page.WaitForTimeoutAsync(1200);
        }

        await Expect(row).ToBeVisibleAsync();

        // Flip the type via the inline select in the type column (first select of the row).
        await row.Locator("fluent-select").First.ClickAsync();
        await row.Locator("fluent-option", new() { HasText = "Disabled" }).ClickAsync();
        await Expect(row.Locator(".type-pill--Disabled")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Rules_and_pricing_tabs_switch_content()
    {
        await Page.GotoAsync("/admin/parking/settings");
        await Expect(Page.GetByText("Ekonomika rezervací")).ToBeVisibleAsync();
        // Let the InteractiveServer circuit hydrate before clicking — a click that lands during
        // the handshake is silently dropped and the tab never switches.
        await Page.WaitForTimeoutAsync(1500);
        await Page.Locator("fluent-tab", new() { HasText = "Důvěra a ochrana" }).ClickAsync();
        await Expect(Page.GetByText("Graf důvěry", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Saving_the_rules_and_pricing_form_confirms_success()
    {
        await Page.GotoAsync("/admin/parking/settings");
        await Expect(Page.GetByText("Ekonomika rezervací")).ToBeVisibleAsync();
        await Page.WaitForTimeoutAsync(1000); // hydrate the InteractiveServer save button
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Uložit nastavení") }).ClickAsync();
        await Expect(Page.GetByText(new Regex("uloženo|saved", RegexOptions.IgnoreCase))).ToBeVisibleAsync();
    }

    [Test]
    public async Task Collusion_review_shows_the_empty_state()
    {
        await Page.GotoAsync("/admin/parking/collusion");
        await Expect(Page.Locator(".empty-state")).ToBeVisibleAsync();
    }
}
