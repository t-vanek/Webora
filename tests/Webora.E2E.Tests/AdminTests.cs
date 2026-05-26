using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Webora.E2E.Tests;

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
        await Expect(Page.Locator(".admin-panel").GetByText("admin@webora.local")).ToBeVisibleAsync();
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
    public async Task Rules_and_pricing_tabs_switch_content()
    {
        await Page.GotoAsync("/admin/parking/settings");
        await Expect(Page.GetByText("Ekonomika rezervací")).ToBeVisibleAsync();
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
