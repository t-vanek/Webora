using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class UiBootstrapTests : AdminTest
{
    [TestCase("/parking", null, null)]
    [TestCase("/admin/users", "#user-search", ".users-results-head > span:first-child")]
    [TestCase("/admin/parking/spots", "#spot-search", ".spots-results-head > span:first-child")]
    public async Task Fluent_controls_initialize_on_direct_navigation(
        string path, string? searchSelector, string? resultsSelector)
    {
        var errors = new List<string>();
        Page.PageError += (_, error) => errors.Add(error);
        Page.Console += (_, message) =>
        {
            if (message.Type == "error") errors.Add(message.Text);
        };

        try
        {
            await Pages.GotoInteractiveAsync(Page, path);
            await Page.WaitForFunctionAsync("""
                () => [...document.querySelectorAll('fluent-button, fluent-anchor, fluent-search, fluent-text-field')]
                    .every(element => element.matches(':defined') && element.shadowRoot)
                """);
            await Expect(Page.Locator("fluent-button button").First).ToBeVisibleAsync();

            if (searchSelector is not null)
            {
                // Typing must reach the server, not just update an inert prerendered input.
                var search = Page.Locator($"{searchSelector} input");
                await search.FillAsync($"missing-{Guid.NewGuid():N}");
                await search.BlurAsync();
                await Expect(Page.Locator(resultsSelector!)).ToHaveTextAsync(
                    new System.Text.RegularExpressions.Regex(@": 0$"));
            }
        }
        finally
        {
            foreach (var error in errors) TestContext.WriteLine(error);
        }
    }
}
