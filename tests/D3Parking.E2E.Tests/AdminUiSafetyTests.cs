using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
[NonParallelizable]
public class AdminUiSafetyTests : AdminTest
{
    [Test]
    public async Task Dialog_keeps_keyboard_focus_and_restores_the_opener()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/visitors");
        var opener = Page.GetByRole(AriaRole.Button, new() { Name = "Nová rezervace pro návštěvu", Exact = true });
        await opener.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        for (var i = 0; i < 20; i++)
        {
            await Page.Keyboard.PressAsync(i < 10 ? "Shift+Tab" : "Tab");
            Assert.That(await dialog.EvaluateAsync<bool>("e => e.contains(document.activeElement)"), Is.True);
        }
        await Page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(opener).ToBeFocusedAsync();
    }

    [Test]
    public async Task Settings_warn_before_leaving_and_keep_the_draft_when_cancelled()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/settings?tab=general");
        var description = Page.GetByRole(AriaRole.Textbox, new() { Name = "Popis webu", Exact = true });
        await description.FillAsync("E2E unsaved description");
        await description.BlurAsync();
        var prompted = new TaskCompletionSource();
        EventHandler<IDialog> handler = async (_, dialog) => { await dialog.DismissAsync(); prompted.TrySetResult(); };
        Page.Dialog += handler;
        try
        {
            await Page.GetByRole(AriaRole.Link, new() { Name = "Návštěvy", Exact = true }).ClickAsync();
            await prompted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(description).ToHaveValueAsync("E2E unsaved description");
            await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/settings"));
        }
        finally { Page.Dialog -= handler; }
    }

    [Test]
    public async Task Saving_another_tab_preserves_the_unsaved_general_draft()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/settings?tab=general");
        var description = Page.GetByRole(AriaRole.Textbox, new() { Name = "Popis webu", Exact = true });
        await description.FillAsync("E2E unsaved description");
        await description.BlurAsync();
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Kódování", Exact = true }).ClickAsync();
        // Save unchanged encoding only; the description remains an unpersisted draft.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Uložit kódování", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".site-settings-page")).ToContainTextAsync("Nastavení bylo uloženo");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Obecné", Exact = true }).ClickAsync();
        await Expect(description).ToHaveValueAsync("E2E unsaved description");
    }

    [Test]
    public async Task Settings_can_discard_a_draft_and_then_navigate_without_a_stale_guard()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/settings?tab=general");
        var description = Page.GetByRole(AriaRole.Textbox, new() { Name = "Popis webu", Exact = true });
        await description.FillAsync("E2E discarded description");
        await description.BlurAsync();
        var prompts = 0;
        EventHandler<IDialog> handler = async (_, dialog) => { prompts++; await dialog.AcceptAsync(); };
        Page.Dialog += handler;
        try
        {
            await Page.GetByRole(AriaRole.Link, new() { Name = "Návštěvy", Exact = true }).ClickAsync();
            await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/parking/visitors$"));
            await Page.GetByRole(AriaRole.Link, new() { Name = "Nastavení webu", Exact = true }).ClickAsync();
            await Expect(Page.Locator(".site-settings-page")).ToBeVisibleAsync();
            await Page.GetByRole(AriaRole.Link, new() { Name = "Návštěvy", Exact = true }).ClickAsync();
            await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/parking/visitors$"));
            Assert.That(prompts, Is.EqualTo(1));
        }
        finally { Page.Dialog -= handler; }
    }

    [Test]
    public async Task Settings_warn_on_browser_back_without_losing_the_draft()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/visitors");
        await Page.GetByRole(AriaRole.Link, new() { Name = "Nastavení webu", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Obecné", Exact = true }).ClickAsync();
        var description = Page.GetByRole(AriaRole.Textbox, new() { Name = "Popis webu", Exact = true });
        await description.FillAsync("E2E back draft");
        await description.BlurAsync();
        var prompted = new TaskCompletionSource();
        EventHandler<IDialog> handler = async (_, dialog) => { await dialog.DismissAsync(); prompted.TrySetResult(); };
        Page.Dialog += handler;
        try
        {
            await Page.EvaluateAsync("history.back()");
            await prompted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Expect(description).ToHaveValueAsync("E2E back draft");
            await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/settings"));
        }
        finally { Page.Dialog -= handler; }
    }

}
