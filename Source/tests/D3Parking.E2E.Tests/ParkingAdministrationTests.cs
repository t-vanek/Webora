using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class ParkingAdministrationTests : AdminTest
{
    [Test]
    public async Task Resident_capacity_is_a_draft_until_saved_and_stale_save_keeps_the_newer_value()
    {
        var code = await Pages.CreateSpotAsync(Page);
        var secondPage = await Context.NewPageAsync();
        try
        {
            await OpenResidentsAsync(Page, code);
            await Page.Locator("#resident-capacity").FillAsync("4");
            await Page.Locator("#resident-capacity").BlurAsync();
            await Expect(Page.Locator("#resident-capacity-save button")).ToBeEnabledAsync();

            // A second circuit reads the database, independently of the first draft.
            await OpenResidentsAsync(secondPage, code);
            await Expect(secondPage.Locator("#resident-capacity")).ToHaveValueAsync("1");
            await secondPage.Locator("#resident-capacity").FillAsync("3");
            await secondPage.Locator("#resident-capacity").BlurAsync();
            await Expect(secondPage.Locator("#resident-capacity-save button")).ToBeEnabledAsync();
            await secondPage.Locator("#resident-capacity-save button").ClickAsync();
            await Expect(secondPage.Locator("#resident-capacity-save button")).ToBeDisabledAsync();

            // The first circuit submits its original rowversion: no silent last-write-wins.
            await Page.Locator("#resident-capacity-save button").ClickAsync();
            await Expect(Page.Locator(".resident-assignment-dialog"))
                .ToContainTextAsync("Záznam mezitím změnil jiný požadavek");
            await Expect(Page.Locator("#resident-capacity")).ToHaveValueAsync("3");
            await OpenResidentsAsync(secondPage, code);
            await Expect(secondPage.Locator("#resident-capacity")).ToHaveValueAsync("3");
        }
        finally
        {
            await secondPage.CloseAsync();
        }
    }

    [Test]
    public async Task Deactivation_explains_retained_bookings_and_cancel_does_not_change_the_spot()
    {
        var code = await Pages.CreateSpotAsync(Page);
        var row = SpotRow(Page, code);
        await row.GetByRole(AriaRole.Button, new() { Name = "Deaktivovat", Exact = true }).ClickAsync();
        var dialog = Page.Locator(".spot-deactivate-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog).ToContainTextAsync(code);
        await Expect(dialog).ToContainTextAsync("automaticky se nezruší ani nevrátí kredit");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Zrušit", Exact = true }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(row.Locator(".state-pill")).ToHaveTextAsync("Aktivní");

        await row.GetByRole(AriaRole.Button, new() { Name = "Deaktivovat", Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Deaktivovat", Exact = true }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(row.Locator(".state-pill")).ToHaveTextAsync("Neaktivní");
    }

    [Test]
    public async Task Board_explains_that_reservations_do_not_prove_physical_presence()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/dashboard");
        await Expect(Page.GetByText(new Regex("Fyzickou přítomnost vozidel aplikace neověřuje")))
            .ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Den", new() { Exact = true })).ToHaveAttributeAsync("type", "date");
    }

    private static ILocator SpotRow(IPage page, string code) =>
        page.Locator(".spots-desktop-list tr", new() { HasText = code });

    private static async Task OpenResidentsAsync(IPage page, string code)
    {
        await Pages.GotoInteractiveAsync(page, "/admin/parking/spots");
        await Pages.SearchSpotsAsync(page, code, 1);
        await SpotRow(page, code).GetByRole(AriaRole.Button,
            new() { Name = "Spravovat rezidenty", Exact = true }).ClickAsync();
        await page.Locator("#resident-capacity").WaitForAsync();
    }
}
