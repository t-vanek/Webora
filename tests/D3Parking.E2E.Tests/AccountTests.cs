using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class AccountTests : AdminTest
{
    [Test]
    public async Task Account_hub_shows_a_profile_card_and_settings_actions()
    {
        await Page.GotoAsync("/account/manage");
        await Expect(Page.Locator(".profile-card")).ToBeVisibleAsync();
        await Expect(Page.Locator(".profile-avatar")).ToContainTextAsync("AD");
        // Seeded from IdentitySeed:AdminDisplayName in appsettings.json.
        await Expect(Page.Locator(".profile-name")).ToContainTextAsync("Administrator");
        await Expect(Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Změnit heslo") })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Profile_groups_settings_into_cards_and_reveals_the_claims_toggle()
    {
        await Page.GotoAsync("/account/profile");
        await Expect(Page.GetByText("Dojezd do práce")).ToBeVisibleAsync();
        // "Tým / oddělení" is both the section heading and the field label.
        await Expect(Page.GetByText("Tým / oddělení").First).ToBeVisibleAsync();
        await Expect(Page.GetByText("Nastavení notifikací")).ToBeVisibleAsync();

        var claims = Page.Locator("details.claims");
        await Expect(claims.Locator("ul")).ToBeHiddenAsync();
        await claims.Locator("summary").ClickAsync();
        await Expect(claims.Locator("code").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Signing_out_returns_to_the_public_header()
    {
        await Page.GotoAsync("/");
        await Expect(Page.Locator(".wallet-chip")).ToBeVisibleAsync();
        // Sign-out moved from the sidebar to the header's avatar menu. The menu is an interactive
        // island, so give the circuit a moment before clicking.
        await Page.WaitForTimeoutAsync(1500);
        await Page.Locator("#account-menu-trigger").ClickAsync();
        await Page.GetByRole(AriaRole.Menuitem, new() { NameRegex = new Regex("Odhlásit") }).ClickAsync();
        // The menu item opens a confirmation page; the actual sign-out is a form post.
        await Pages.SubmitAsync(Page);
        await Expect(Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Přihlásit") })).ToBeVisibleAsync();
        await Expect(Page.Locator(".wallet-chip")).ToHaveCountAsync(0);
    }
}
