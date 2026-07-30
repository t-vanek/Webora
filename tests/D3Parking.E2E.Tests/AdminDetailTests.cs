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
        // plain text lookup would match both it and the role. The role label is localized, which
        // is what distinguishes the two ("Administrátor" vs the untranslated display name).
        await Expect(Page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new Regex("Administrátor") }))
            .ToBeVisibleAsync();
        // Status changes and deletion are not offered for your own account.
        await Expect(Page.Locator(".danger-zone")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Editing_a_built_in_role_shows_its_groups_and_their_union_read_only()
    {
        await Page.GotoAsync("/admin/roles");
        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Zaměstnanec") }).First.ClickAsync();

        await Expect(Page.Locator(".profile-section").First).ToBeVisibleAsync();
        // A role is composed of groups; the picker shows them, ticked.
        await Expect(Page.Locator(".group-pick--on").First).ToBeVisibleAsync();
        await Expect(Page.Locator(".group-picker__grid")).ToContainTextAsync("Přístup k parkovišti");
        // …and the panel underneath shows what those groups add up to, which is what the role grants.
        await Expect(Page.Locator(".group-picker__effective")).ToContainTextAsync("Rezervovat místo");
        // Built-in roles are seeder-managed: no save button, and the members section is present.
        await Expect(Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Uložit$") })).ToHaveCountAsync(0);
        await Expect(Page.GetByText("Členové")).ToBeVisibleAsync();
    }

    [Test]
    public async Task A_built_in_permission_group_lists_its_permissions_and_the_roles_using_it()
    {
        await Page.GotoAsync("/admin/permission-groups");
        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Přístup k parkovišti") }).First.ClickAsync();

        // The canonical id stays on screen beside the human label, so the two can be matched up.
        await Expect(Page.GetByText("Parking.ViewLeaderboard")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Zobrazit žebříček").First).ToBeVisibleAsync();
        // Built-in groups are seeder-managed and read-only.
        await Expect(Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Uložit$") })).ToHaveCountAsync(0);
        // Which roles carry the group is the blast radius of any edit, so it is on the same page.
        await Expect(Page.GetByText("Použito v rolích")).ToBeVisibleAsync();
        await Expect(Page.Locator(".role-members")).ToContainTextAsync("Zaměstnanec");
    }

    [Test]
    public async Task Creating_a_user_shows_the_form_card_with_role_options()
    {
        await Page.GotoAsync("/admin/users/create");
        await Expect(Page.Locator(".profile-section")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Administrátor")).ToBeVisibleAsync(); // a role checkbox
        await Expect(Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Nový účet") }))
            .ToBeVisibleAsync();
    }
}
