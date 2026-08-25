using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class ParkingTests : AdminTest
{
    [Test]
    public async Task Resident_spot_plan_follows_the_week_planner_and_starts_collapsed()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");

        var planner = Page.Locator(".parking-planner");
        var spotPlan = Page.Locator(".resident-plan-card");
        var handoff = Page.Locator(".handoff-card");
        var calendar = Page.Locator(".calendar-subscription");

        await Expect(planner).ToBeVisibleAsync();
        await Expect(spotPlan).ToBeVisibleAsync();
        await Expect(handoff).ToBeVisibleAsync();
        await Expect(calendar).ToBeVisibleAsync();
        var sectionOrder = await Page
            .Locator(".parking-planner, .resident-plan-card, .handoff-card, .calendar-subscription")
            .EvaluateAllAsync<string[]>(
                "elements => elements.map(element => element.classList.contains('parking-planner') ? 'planner' : "
                + "element.classList.contains('resident-plan-card') ? 'plan' : "
                + "element.classList.contains('handoff-card') ? 'handoff' : 'calendar')");
        Assert.That(sectionOrder, Is.EqualTo(new[] { "planner", "plan", "handoff", "calendar" }));

        var sections = spotPlan.Locator("details.owned-section");
        await Expect(sections).ToHaveCountAsync(2);
        Assert.That(await sections.EvaluateAllAsync<bool>(
            "elements => elements.every(element => !element.open)"), Is.True);

        await sections.First.Locator("summary").ClickAsync();
        Assert.That(await sections.First.EvaluateAsync<bool>("element => element.open"), Is.True);
        await Expect(sections.First.Locator("input[type=date]").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Resident_alternative_search_appears_before_secondary_cards()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");

        await EnsureSharedSearchIsVisibleAsync();

        var searchBar = Page.Locator(".booking-bar");
        await Expect(searchBar).ToBeVisibleAsync();
        var sectionOrder = await Page
            .Locator(".resident-plan-card, .reserve-layout, .handoff-card, .calendar-subscription")
            .EvaluateAllAsync<string[]>(
                "elements => elements.map(element => element.classList.contains('resident-plan-card') ? 'plan' : "
                + "element.classList.contains('reserve-layout') ? 'search' : "
                + "element.classList.contains('handoff-card') ? 'handoff' : 'calendar')");
        Assert.That(sectionOrder, Is.EqualTo(new[] { "plan", "search", "handoff", "calendar" }));
    }

    [Test]
    public async Task Resident_sees_only_the_named_offer_for_a_day_assigned_to_them()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");

        var handoffCard = Page.Locator(".handoff-card");
        await Expect(handoffCard.GetByRole(AriaRole.Heading, new() { Name = "Předání místa" }))
            .ToBeVisibleAsync();
        await Expect(handoffCard.GetByRole(AriaRole.Heading, new() { Name = "Požádat rezidenta o místo" }))
            .ToHaveCountAsync(0);

        var assignedDay = Page.Locator(".parking-planner__day:not(.is-reservation-blocked):not(.is-unavailable)")
            .Filter(new() { HasText = "Přiděleno tobě" }).First;
        await Expect(assignedDay).ToBeVisibleAsync();
        var releaseActionLabel = (await assignedDay.GetAttributeAsync("class"))?.Contains("is-today") == true
            ? "Uvolnit tento den"
            : "Spravovat uvolnění";
        await assignedDay.ClickAsync();

        var detailDialog = Page.Locator(".parking-day-dialog");
        await Expect(detailDialog).ToBeVisibleAsync();
        await Expect(detailDialog.GetByRole(AriaRole.Button,
            new() { Name = releaseActionLabel, Exact = true })).ToBeVisibleAsync();
        await detailDialog.GetByRole(AriaRole.Button, new() { Name = "Zavřít", Exact = true }).ClickAsync();
        await Expect(assignedDay).ToBeFocusedAsync();
        await Expect(Page.Locator(".resident-day-actions")).ToHaveCountAsync(0);

        await Expect(handoffCard.GetByRole(AriaRole.Heading, new() { Name = "Předat vlastní místo" }))
            .ToBeVisibleAsync();
        await Expect(handoffCard.Locator(".handoff-card__window")).ToContainTextAsync("celý den");
    }

    [Test]
    public async Task Resident_planner_keeps_named_allocations_beyond_the_release_plan_horizon()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");

        // The release plan is intentionally shorter than the booking horizon. Moving five weeks
        // ahead reproduces the range that previously fell back to the contradictory "Bez rezervace".
        var nextWeek = Page.GetByRole(AriaRole.Button, new() { Name = "Následující týden" });
        for (var week = 0; week < 5; week++)
        {
            await nextWeek.ClickAsync();
        }

        var weekDays = Page.Locator(".parking-planner__day");
        await Expect(weekDays).ToHaveCountAsync(7);
        var allowedDays = weekDays.Locator(":scope:not(.is-reservation-blocked):not(.is-unavailable)");
        var blockedDays = weekDays.Locator(":scope.is-reservation-blocked");
        Assert.That(await allowedDays.CountAsync() + await blockedDays.CountAsync(), Is.EqualTo(7));
        Assert.That(await allowedDays.Locator(".parking-planner__booking[class*='resident-']").CountAsync(),
            Is.EqualTo(await allowedDays.CountAsync()));
        Assert.That(await blockedDays.Locator(".parking-planner__empty--restricted").CountAsync(),
            Is.EqualTo(await blockedDays.CountAsync()));
        await Expect(weekDays.GetByText("Stav místa není dostupný", new() { Exact = true })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Planner_labels_a_booking_on_another_spot_as_another_booking()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");

        var otherSpotCode = Page.Locator(".parking-planner__booking > strong")
            .Filter(new() { HasNotText = "D3-1" }).First;
        await Expect(otherSpotCode).ToBeVisibleAsync();
        var code = (await otherSpotCode.InnerTextAsync()).Trim();
        var day = otherSpotCode.Locator("xpath=ancestor::button[contains(@class, 'parking-planner__day')]");
        await day.ClickAsync();

        var detailDialog = Page.Locator(".parking-day-dialog");
        await Expect(detailDialog).ToBeVisibleAsync();
        await Expect(detailDialog.GetByRole(AriaRole.Heading,
            new() { Name = "Tvoje další rezervace v tento den", Exact = true })).ToBeVisibleAsync();
        await Expect(detailDialog.GetByText(new Regex("netýkají rezidentního místa D3-1")))
            .ToBeVisibleAsync();
        await Expect(detailDialog.GetByText(code, new() { Exact = true })).ToBeVisibleAsync();
        await Expect(detailDialog.GetByRole(AriaRole.Heading, new() { Name = "Rezervace", Exact = true }))
            .ToHaveCountAsync(0);
    }

    [Test]
    public async Task Planner_distinguishes_a_forbidden_weekday_from_the_booking_horizon()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");

        var horizonStatus = Page.Locator(".parking-planner__horizon");
        await Expect(horizonStatus).ToBeVisibleAsync();
        await Expect(horizonStatus).ToContainTextAsync(
            new Regex(@"Rezervace do \d{1,2}\.\s?\d{1,2}\.\s?\d{4} včetně"));

        var blockedDays = Page.Locator(".parking-planner__day.is-reservation-blocked");
        await Expect(blockedDays.First).ToBeVisibleAsync();
        var allDays = Page.Locator(".parking-planner__day");
        var dayHeights = await allDays.EvaluateAllAsync<int[]>(
            "elements => elements.map(element => Math.round(element.getBoundingClientRect().height))");
        var surfaceHeights = await allDays.EvaluateAllAsync<int[]>(
            "elements => elements.map(element => Math.round(element.querySelector('.parking-planner__booking, .parking-planner__empty').getBoundingClientRect().height))");
        Assert.That(dayHeights.Distinct().Count(), Is.EqualTo(1),
            "Every day tile in the week must have the same height.");
        Assert.That(surfaceHeights.Distinct().Count(), Is.EqualTo(1),
            "Every state surface, including a forbidden day, must have the same height.");
        var blockedDayCount = await blockedDays.CountAsync();
        Assert.That(blockedDayCount, Is.GreaterThan(0));
        Assert.That(await blockedDays.GetByText("Rezervace nepovolena", new() { Exact = true }).CountAsync(),
            Is.EqualTo(blockedDayCount));
        Assert.That(await blockedDays.GetByText(
                new Regex("^(Dnešní den|Nepovolený den|Víkend|Státní svátek)$")).CountAsync(),
            Is.EqualTo(blockedDayCount));
        Assert.That(await blockedDays.Locator(".parking-planner__empty--restricted .parking-planner__status-icon").CountAsync(),
            Is.EqualTo(blockedDayCount), "Every forbidden-day label must carry its lock icon.");
        await Expect(blockedDays.Locator(".parking-planner__booking")).ToHaveCountAsync(0);
        await Expect(blockedDays.GetByText(new Regex("Přiděleno|D3-1"))).ToHaveCountAsync(0);
        var blockedAria = await blockedDays.First.GetAttributeAsync("aria-label");
        Assert.That(blockedAria, Does.Not.Contain("D3-1").And.Not.Contain("Přiděleno"));
        await Expect(blockedDays.GetByText("Mimo rezervační období", new() { Exact = true }))
            .ToHaveCountAsync(0);

        await Expect(Page.Locator(".parking-planner__detail-trigger")).ToHaveCountAsync(0);
        Assert.That(await allDays.EvaluateAllAsync<bool>(
            "elements => elements.every(element => element.getAttribute('aria-haspopup') === 'dialog')"),
            Is.True, "Every day tile must open its detail dialog directly.");
        var blockedDetailTrigger = blockedDays.First;
        await blockedDetailTrigger.ClickAsync();
        var detailDialog = Page.Locator(".parking-day-dialog");
        await Expect(detailDialog).ToBeVisibleAsync();
        await Expect(detailDialog).ToBeFocusedAsync();
        await Expect(detailDialog.GetByRole(AriaRole.Heading, new() { Name = "Detail parkování" }))
            .ToBeVisibleAsync();
        await Expect(detailDialog.GetByText("Rezervace nepovolena", new() { Exact = true }))
            .ToBeVisibleAsync();
        await Expect(detailDialog.GetByText(new Regex("D3-1|Přidělený rezident|Administrátor|E2E Test")))
            .ToHaveCountAsync(0);

        await Page.Keyboard.PressAsync("Escape");
        await Expect(detailDialog).ToHaveCountAsync(0);
        await Expect(blockedDetailTrigger).ToBeFocusedAsync();

        await Expect(Page.Locator(".resident-day-actions")).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Spravovat uvolnění", Exact = true }))
            .ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Najít jiné místo", Exact = true }))
            .ToHaveCountAsync(0);
        await Expect(Page.Locator(".handoff-action-card--blocked")).ToBeVisibleAsync();
        await Expect(Page.Locator(".handoff-action-card--blocked"))
            .Not.ToContainTextAsync("SelectedBookingRestrictionText");
        await Expect(Page.Locator(".handoff-card").GetByRole(AriaRole.Heading,
            new() { Name = "Předat vlastní místo" })).ToHaveCountAsync(0);

        var allowedDay = Page.Locator(
            ".parking-planner__day:not(.is-reservation-blocked):not(.is-unavailable)").First;
        var allowedDetailTrigger = allowedDay;
        await allowedDetailTrigger.ClickAsync();
        await Expect(detailDialog).ToBeVisibleAsync();
        await Expect(detailDialog.GetByText("D3-1", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(detailDialog.GetByText("Zdroj", new() { Exact = true })).ToBeVisibleAsync();
        await detailDialog.GetByRole(AriaRole.Button, new() { Name = "Zavřít", Exact = true }).Last.ClickAsync();
        await Expect(detailDialog).ToHaveCountAsync(0);
        await Expect(allowedDetailTrigger).ToBeFocusedAsync();

        // Read the configured rolling horizon from the same date input users receive, then move
        // far enough that the whole displayed week is genuinely beyond it. This remains valid for
        // both the default short horizon and an administrator-configured full year.
        await EnsureSharedSearchIsVisibleAsync();
        var dateInput = Page.Locator(".booking-bar__row input[type=date]");
        var first = DateOnly.ParseExact((await dateInput.GetAttributeAsync("min"))!, "yyyy-MM-dd");
        var last = DateOnly.ParseExact((await dateInput.GetAttributeAsync("max"))!, "yyyy-MM-dd");
        var displayedHorizon = Regex.Match(
            await horizonStatus.InnerTextAsync(),
            @"(\d{1,2})\.\s?(\d{1,2})\.\s?(\d{4})");
        Assert.That(displayedHorizon.Success, Is.True);
        Assert.That(
            new DateOnly(
                int.Parse(displayedHorizon.Groups[3].Value),
                int.Parse(displayedHorizon.Groups[2].Value),
                int.Parse(displayedHorizon.Groups[1].Value)),
            Is.EqualTo(last),
            "The planner status must name the same inclusive boundary as the booking input.");
        var weeksToOutside = (last.DayNumber - first.DayNumber) / 7 + 2;
        var nextWeek = Page.GetByRole(AriaRole.Button, new() { Name = "Následující týden" });
        for (var week = 0; week < weeksToOutside; week++)
        {
            await nextWeek.ClickAsync();
        }

        var outsideDays = Page.Locator(".parking-planner__day.is-outside-horizon");
        await Expect(outsideDays).ToHaveCountAsync(7);
        await Expect(outsideDays.GetByText("Mimo rezervační období", new() { Exact = true }))
            .ToHaveCountAsync(7);
        await Expect(outsideDays.Locator(".parking-planner__status-icon")).ToHaveCountAsync(7);
        await Expect(outsideDays.Locator(".parking-planner__booking")).ToHaveCountAsync(0);
        await Expect(outsideDays.GetByText(new Regex("^Rezervace nepovoleny")))
            .ToHaveCountAsync(0);

        var outsideDetailTrigger = outsideDays.First;
        await outsideDetailTrigger.ClickAsync();
        await Expect(detailDialog).ToBeVisibleAsync();
        await Expect(detailDialog.GetByText("Termín není dostupný", new() { Exact = true }))
            .ToBeVisibleAsync();
        await Expect(detailDialog.GetByText("Mimo rezervační období", new() { Exact = true }))
            .ToBeVisibleAsync();
        await Expect(detailDialog.GetByText(new Regex("D3-1|Přidělený rezident|Administrátor|E2E Test")))
            .ToHaveCountAsync(0);
    }

    [Test]
    public async Task Achievements_page_shows_only_personal_achievements()
    {
        await Page.GotoAsync("/parking/achievements");
        await Expect(Page.Locator(".contribution-ring")).ToBeVisibleAsync();
        await Expect(Page.Locator(".hero__count")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Moje ocenění" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".admin-panel, .empty-state").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Searching_a_window_shows_a_price_quote()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");
        await EnsureSharedSearchIsVisibleAsync();
        await Expect(Page.Locator(".booking-bar__row input[type=date]")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Najít") }).ClickAsync();
        await Expect(Page.Locator(".price-tag__num")).ToBeVisibleAsync();
        await Expect(Page.Locator(".quote-meter .meter")).ToBeVisibleAsync();
        await Expect(Page.Locator(".quote-panel .pill")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Reserving_a_future_window_round_trips_the_picked_time()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");
        await EnsureSharedSearchIsVisibleAsync();
        var bar = Page.Locator(".booking-bar__row");
        await Expect(bar.Locator("input[type=date]")).ToBeVisibleAsync();

        // A few days out (never "in the past") at a two-digit hour, randomised so
        // re-runs don't collide on an already-booked spot.
        var day = DateTime.UtcNow.Date.AddDays(2 + Random.Shared.Next(8));
        var iso = day.ToString("yyyy-MM-dd");
        var hour = 10 + Random.Shared.Next(5); // 10..14
        var hh = hour.ToString("D2");

        var date = bar.Locator("input[type=date]");
        await date.FillAsync(iso);
        await date.BlurAsync();
        await bar.Locator("input[type=time]").Nth(0).FillAsync($"{hh}:00");
        await bar.Locator("input[type=time]").Nth(0).BlurAsync();
        await bar.Locator("input[type=time]").Nth(1).FillAsync($"{hh}:30");
        await bar.Locator("input[type=time]").Nth(1).BlurAsync();
        await Expect(date).ToHaveValueAsync(iso);

        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Najít") }).ClickAsync();
        var firstSpot = Page.Locator(".spot-card").First;
        await Expect(firstSpot).ToBeVisibleAsync();
        await firstSpot.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Rezervovat") }).ClickAsync();

        // My reservations live on the home dashboard. The window is stored as the picked wall-clock
        // in UTC, so that list must show exactly the picked time — this guards the timezone regression.
        await Pages.GotoInteractiveAsync(Page, "/");

        // Cancelled leftovers from earlier runs can carry the same random slot, so the row is pinned
        // down as the one still holding a Zrušit button: only the fresh Reserved booking has one.
        var csDate = day.ToString("dd.MM.yyyy");
        var cancelButton = Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Zrušit") });
        var row = Page.GetByRole(AriaRole.Row, new() { NameRegex = new Regex($"{Regex.Escape(csDate)} {hour}:00") })
            .Filter(new() { Has = cancelButton });
        try
        {
            await Expect(row).ToBeVisibleAsync();
        }
        finally
        {
            // Clean up even when the assertion fails: a leaked reservation keeps blocking its
            // random slot for future runs and slowly drains the admin wallet until the Reserve
            // button disables (which is exactly how this suite once ground to a halt). This far
            // ahead of the start the charge refunds in full.
            if (await row.CountAsync() > 0)
            {
                await row.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Zrušit") }).ClickAsync();
                await Expect(row).ToHaveCountAsync(0);
            }
        }
    }

    private async Task EnsureSharedSearchIsVisibleAsync()
    {
        // The resident schedule loads after hydration. Search actions now live in the day detail,
        // so open the selected tile and use the same canonical route as the driver.
        await Expect(Page.Locator(".parking-planner")).ToBeVisibleAsync();
        var searchBar = Page.Locator(".booking-bar__row");
        if (!await searchBar.IsVisibleAsync())
        {
            await Page.Locator(".parking-planner__day.is-selected").ClickAsync();
            var detailDialog = Page.Locator(".parking-day-dialog");
            await Expect(detailDialog).ToBeVisibleAsync();
            await detailDialog.GetByRole(AriaRole.Button,
                new() { NameRegex = new Regex("Najít (jiné )?místo") }).ClickAsync();
        }

        await Expect(searchBar).ToBeVisibleAsync();

        var plannerHelp = Page.Locator(".parking-planner__head p");
        var expectedHelp = await Page.Locator(".booking-bar input[type=time]").CountAsync() > 0
            ? "Vyber den, nastav čas od–do a naplánuj dostupné místo."
            : "Vyber den a naplánuj dostupné místo na celý den.";
        await Expect(plannerHelp).ToHaveTextAsync(expectedHelp);
    }
}
