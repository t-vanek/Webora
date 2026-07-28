using System.Text.RegularExpressions;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class AuthTests : AnonymousTest
{
    [Test]
    public async Task Login_page_shows_the_split_brand_panel_and_form()
    {
        await Page.GotoAsync("/login");
        await Expect(Page.Locator(".auth-shell__brand")).ToBeVisibleAsync();
        await Expect(Page.Locator("fluent-text-field[name='Input.Email']")).ToBeVisibleAsync();
        await Expect(Page.Locator("fluent-text-field[name='Input.Password']")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Invalid_credentials_show_an_error()
    {
        await Pages.LoginAsync(Page, Admin.Email, "WrongPassword1!");
        await Expect(Page.GetByText(new Regex("Neplatný e-mail nebo heslo|Invalid", RegexOptions.IgnoreCase)))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Valid_credentials_sign_in_and_land_on_the_dashboard()
    {
        await Pages.LoginAsync(Page);
        await Expect(Page.Locator(".wallet-chip")).ToBeVisibleAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/$"));
        await Expect(Page.Locator(".home-hero__title")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Registration_creates_an_account_and_shows_the_activation_notice()
    {
        await Page.GotoAsync("/register");
        var email = $"e2e+{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}@example.com";
        await Pages.FluentField(Page, "Input.DisplayName").FillAsync("E2E Test");
        await Pages.FluentField(Page, "Input.Email").FillAsync(email);
        await Pages.FluentField(Page, "Input.Password").FillAsync("Heslo12345!");
        await Pages.FluentField(Page, "Input.ConfirmPassword").FillAsync("Heslo12345!");
        await Pages.SubmitAsync(Page);
        await Expect(Page.GetByText(new Regex("aktivační odkaz|activation link", RegexOptions.IgnoreCase)))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Registration_rejects_mismatched_passwords()
    {
        await Page.GotoAsync("/register");
        var email = $"e2e+{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}@example.com";
        await Pages.FluentField(Page, "Input.Email").FillAsync(email);
        await Pages.FluentField(Page, "Input.Password").FillAsync("Heslo12345!");
        await Pages.FluentField(Page, "Input.ConfirmPassword").FillAsync("Different1!");
        await Pages.SubmitAsync(Page);
        await Expect(Page.GetByText(new Regex("neshoduj|match", RegexOptions.IgnoreCase))).ToBeVisibleAsync();
    }

    [Test]
    public async Task Anonymous_visitor_is_sent_to_login_for_an_admin_page()
    {
        await Page.GotoAsync("/admin/users");
        await Expect(Pages.FluentField(Page, "Input.Email")).ToBeVisibleAsync();
    }
}
