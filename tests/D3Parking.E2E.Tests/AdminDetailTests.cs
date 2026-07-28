using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class AdminDetailTests : AdminTest
{
    [Test]
    public async Task Editing_own_account_shows_the_profile_card_and_hides_the_danger_zone()
    {
        await Page.GotoAsync("/admin/users");
        await Page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex("admin@d3parking.local") })
            .GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Spravovat") })
            .ClickAsync();

        await Expect(Page.Locator(".profile-card")).ToBeVisibleAsync();
        // Scoped to the checkbox: the seeded admin's display name is "Administrator" too, so a
        // plain text lookup would match both it and the role.
        await Expect(Page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new Regex("Administrator") }))
            .ToBeVisibleAsync();
        // Status changes and deletion are not offered for your own account.
        await Expect(Page.Locator(".danger-zone")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Editing_a_role_shows_the_permission_matrix_in_a_panel()
    {
        await Page.GotoAsync("/admin/roles");
        await Page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex("Editor") })
            .GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Spravovat") })
            .ClickAsync();

        await Expect(Page.GetByText("Oprávnění").First).ToBeVisibleAsync();
        await Expect(Page.Locator(".profile-section")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Parking.Reserve")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Creating_a_user_shows_the_form_card_with_role_options()
    {
        await Page.GotoAsync("/admin/users/create");
        await Expect(Page.Locator(".profile-section")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Administrator")).ToBeVisibleAsync(); // a role checkbox
        await Expect(Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Nový účet") }))
            .ToBeVisibleAsync();
    }
}
