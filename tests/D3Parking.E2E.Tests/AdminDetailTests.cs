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
        await Page.Locator(".users-table tr", new() { HasText = "admin@d3parking.local" })
            .Locator("a.users-identity")
            .ClickAsync();

        await Expect(Page.Locator(".user-detail-hero")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameString = "Přístupy" }).ClickAsync();
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
    public async Task The_members_of_a_role_sit_beside_its_composition_and_can_be_searched()
    {
        // The members used to be a bare list stacked under a picker as tall as the group catalog.
        // Beside it now, as a searchable, paged table — "who does this change hit" is the question
        // asked while the composition is being edited, not after scrolling past it.
        // The catalog is read for the role's address, then the detail is opened with the circuit
        // attached rather than clicked through: the search box is an island, and a term typed
        // before the circuit is up goes nowhere at all. (Clicking through to a role is what the
        // spec above covers.) The seeded administrator holds this role, so it always has a member.
        await Page.GotoAsync("/admin/roles");
        var href = await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Administrátor") })
            .First.GetAttributeAsync("href");
        await Pages.GotoInteractiveAsync(Page, "/" + href!.TrimStart('/'));

        var side = Page.Locator(".role-split__side");
        await Expect(side).ToBeVisibleAsync();
        await Expect(side).ToContainTextAsync("admin@d3parking.local");

        // The search reaches the server, so a term that matches nobody empties the table rather
        // than leaving the first page sitting there.
        var search = Page.Locator("fluent-search#role-member-search input");
        await search.FillAsync("nikdo-takový");
        await Expect(side.Locator(".empty-state")).ToBeVisibleAsync();

        await search.FillAsync("admin@");
        await Expect(side).ToContainTextAsync("admin@d3parking.local");
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
