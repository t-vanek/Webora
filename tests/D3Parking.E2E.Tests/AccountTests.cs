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

        // The hub carries the vehicle self-service (plate + fleet pairing entry point)...
        await Expect(Page.GetByText("Vozidlo", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator("fluent-text-field[name='VehicleInput.Plate']")).ToBeVisibleAsync();

        // ...the security actions with their email-second-factor note...
        await Expect(Page.GetByText("Zabezpečení a přihlášení")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Změnit heslo") })).ToBeVisibleAsync();

        // ...and the destructive actions fenced off in the danger zone.
        await Expect(Page.Locator(".danger-zone")).ToContainTextAsync("Deaktivovat účet");
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
        // Sign-out moved from the sidebar to the header's avatar menu.
        await Pages.GotoInteractiveAsync(Page, "/");
        await Expect(Page.Locator(".wallet-chip")).ToBeVisibleAsync();

        // A forged form without the cookie-bound request token is rejected and must not clear the
        // authenticated session. This exercises the endpoint metadata, not just the rendered form.
        var forgedForm = Page.APIRequest.CreateFormData();
        forgedForm.Set("returnUrl", "/");
        var rejected = await Page.APIRequest.PostAsync("/account/signout", new()
        {
            Form = forgedForm,
        });
        Assert.That(rejected.Status, Is.EqualTo(400));
        await Pages.GotoInteractiveAsync(Page, "/");
        await Expect(Page.Locator(".wallet-chip")).ToBeVisibleAsync();

        // The native account menu is available before the interactive circuit and the sign-out
        // button posts a framework-generated antiforgery token directly to the endpoint.
        await Page.Locator("#account-menu-trigger").ClickAsync();
        await Expect(Page.Locator(".account-menu-submit")).ToBeVisibleAsync();
        await Expect(Page.Locator(
            "form[action='/account/signout'] input[name='__RequestVerificationToken']"))
            .ToHaveCountAsync(1);
        var signOutResponse = await Page.RunAndWaitForResponseAsync(
            () => Page.Locator(".account-menu-submit").ClickAsync(),
            response => response.Url.EndsWith("/account/signout", StringComparison.Ordinal));
        Assert.That(signOutResponse.Status, Is.EqualTo(302));

        // The anonymous landing has its own "Přihlásit se" hero button, so target the header link
        // by class — the assertion is about the public header coming back.
        await Expect(Page.Locator(".header-nav-link[href='login']")).ToBeVisibleAsync();
        await Expect(Page.Locator(".wallet-chip")).ToHaveCountAsync(0);
        Assert.That(Page.Url, Does.Not.Contain("/logout"));
    }
}
