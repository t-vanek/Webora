using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
public class AdminTests : AdminTest
{
    [Test]
    public async Task Users_list_shows_the_header_chip_panel_and_create_action()
    {
        await Page.GotoAsync("/admin/users");
        await Expect(Page.Locator(".count-chip")).ToBeVisibleAsync();
        await Expect(Page.Locator(".admin-panel")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("Nový účet") })).ToBeVisibleAsync();
        await Expect(Page.Locator(".admin-panel").GetByText("admin@d3parking.local")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Roles_list_renders_the_seeded_roles_as_cards()
    {
        await Page.GotoAsync("/admin/roles");
        await Expect(Page.Locator(".count-chip")).ToBeVisibleAsync();
        // Scoped to the roles grid: roles and permission groups are two tabs of one page now, both
        // rendered at once (FluentTabs needs its panels present), so a bare ".role-cards" matches
        // the groups grid as well.
        await Expect(Page.Locator(".role-cards--roles")).ToContainTextAsync("Administrátor");
        // Each built-in role states what it is for, which is the point of the card view.
        await Expect(Page.Locator(".role-cards--roles")).ToContainTextAsync("Správce parkoviště");
    }

    [Test]
    public async Task Permission_groups_open_on_their_own_tab_and_the_search_spans_both_catalogs()
    {
        // The groups route keeps its own identity — same page, other tab.
        await Pages.GotoInteractiveAsync(Page, "/admin/permission-groups");
        await Expect(Page.Locator(".role-cards--groups")).ToContainTextAsync("Přístup k parkovišti");

        // A permission typed once is answered on both tabs at the same time, which is the point of
        // putting the two catalogs together: two roles carry it, and it comes from two groups.
        await Pages.GotoInteractiveAsync(Page, "/admin/roles?q=ManageSpots");
        await Expect(Page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex(@"^Role \(2\)$") })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex(@"^Skupiny \(2\)$") })).ToBeVisibleAsync();
        await Expect(Page.Locator(".role-cards--roles")).ToContainTextAsync("Správce parkoviště");
    }

    [Test]
    public async Task Roles_matrix_view_shows_a_column_per_permission_group()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/roles");
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Matice$") }).ClickAsync();

        await Expect(Page.Locator(".role-matrix")).ToBeVisibleAsync();
        await Expect(Page.Locator(".role-matrix thead")).ToContainTextAsync("Parkoviště");
        await Expect(Page.Locator(".role-matrix thead")).ToContainTextAsync("Nastavení");
    }

    [Test]
    public async Task Settings_entra_tab_renders_the_sign_in_and_provisioning_sections()
    {
        // Deep-linked, which is the same route the directory page offers when nothing is configured.
        await Pages.GotoInteractiveAsync(Page, "/admin/settings?tab=entra");

        await Expect(Page.Locator(".site-settings-page.settings-page")).ToBeVisibleAsync();
        await Expect(Page.Locator(".site-settings-overview")).ToHaveCountAsync(0);
        var entraPanel = Page.Locator(".settings-panel").Filter(new() { HasText = "Client secret aplikace" });
        await Expect(entraPanel).ToBeVisibleAsync();
        await Expect(entraPanel.Locator(".settings-savebar")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Přihlašování přes Entra ID", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByText("Provisioning (SCIM)").First).ToBeVisibleAsync();
        await Expect(Page.GetByText(new Regex("Povinný pouze pro zapnuté přihlašování"))).ToBeVisibleAsync();
        await Page.Locator("#entra-client-secret-help").FocusAsync();
        await Expect(Page.GetByText(new Regex("Nejde o MFA ani jednorázový kód"))).ToBeVisibleAsync();
        await Page.Locator("#entra-scim-token-help").FocusAsync();
        await Expect(Page.GetByText(new Regex("Není to client secret registrace aplikace"))).ToBeVisibleAsync();

        // The secret is write-only: the page may say one is missing, never show one.
        await Expect(Page.GetByText("Zatím není uloženo.").First).ToBeVisibleAsync();

        // The two values whoever fills in the app registration has to copy out of here.
        await Expect(Page.Locator("code").Filter(new() { HasTextRegex = new Regex("/signin-entra$") })).ToBeVisibleAsync();
        await Expect(Page.Locator("code").Filter(new() { HasTextRegex = new Regex("/scim/v2$") })).ToBeVisibleAsync();

        await Expect(Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Uložit Entra ID") })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Settings_domain_tab_uses_the_same_single_column_field_template_as_parking_rules()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/settings?tab=domain");

        var panel = Page.Locator(".settings-panel").First;
        await Expect(panel).ToBeVisibleAsync();
        await Expect(panel.Locator(".planner-setting-row")).ToHaveCountAsync(10);
        await Expect(panel.Locator(".planner-tooltip-trigger")).ToHaveCountAsync(10);
        await Expect(panel.Locator(".settings-section__title svg")).ToHaveCountAsync(2);

        // Text, number, select and textarea controls all occupy the same 240 px template column.
        var widths = await panel
            .Locator(".planner-setting-row > fluent-text-field, .planner-setting-row > fluent-number-field, .planner-setting-row > fluent-select, .planner-setting-row > fluent-text-area")
            .EvaluateAllAsync<double[]>("elements => elements.map(element => element.getBoundingClientRect().width)");
        Assert.That(widths, Is.Not.Empty);
        Assert.That(widths.All(width => Math.Abs(width - widths[0]) < 1), Is.True,
            $"Rendered control widths: {string.Join(", ", widths.Select(width => width.ToString("0.##")))}");
    }

    [Test]
    public async Task The_directory_role_menu_opens_clear_of_its_panel()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/directory");
        var addPanel = Page.Locator(".directory-add");
        for (var attempt = 0; attempt < 3 && !await addPanel.IsVisibleAsync(); attempt++)
        {
            await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Nové mapování|New mapping") }).ClickAsync();
            try
            {
                await addPanel.WaitForAsync(new() { Timeout = 2000 });
            }
            catch (TimeoutException)
            {
                // The first interactive re-render can replace the server-rendered button.
            }
        }
        await Expect(addPanel).ToBeVisibleAsync();
        await Expect(Page.Locator(".directory-add-backdrop")).ToBeVisibleAsync();
        await Expect(addPanel).ToHaveAttributeAsync("role", "dialog");
        await Expect(addPanel).ToHaveAttributeAsync("aria-modal", "true");

        // A Fluent label is a sibling of its input, not its parent, so a wrapping flex row used to
        // strand "Role v aplikaci" at the end of the line above — where it read as a label for the
        // app-role text field, with its own menu alone underneath.
        var field = Page.Locator(".directory-add__field")
            .Filter(new() { Has = Page.Locator("fluent-select") });
        await Expect(field).ToContainTextAsync("Role v aplikaci");

        // The panel is not a FluentCard, because fluent-card's contain:content clipped the opened
        // listbox to the card edge and left the options underneath unclickable. Picking one is the
        // assertion — a clipped menu cannot be clicked through.
        await Page.Locator("fluent-select").First.ClickAsync();
        await Page.GetByRole(AriaRole.Option, new() { NameRegex = new Regex("^Zaměstnanec$") }).ClickAsync();
        await Expect(Page.Locator("fluent-select").First).ToContainTextAsync("Zaměstnanec");
    }

    [Test]
    public async Task Parking_spots_list_shows_type_and_state_pills()
    {
        await Page.GotoAsync("/admin/parking/spots");
        await Expect(Page.Locator(".type-pill").First).ToBeVisibleAsync();
        await Expect(Page.Locator(".state-pill").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Spot_generator_creates_a_series_and_marks_it_duplicate_afterwards()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/spots");
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Přidat místa|Add spots") }).ClickAsync();
        await Expect(Page.Locator(".spots-create-backdrop")).ToBeVisibleAsync();
        await Expect(Page.Locator(".spots-create-dialog")).ToHaveAttributeAsync("role", "dialog");
        await Expect(Page.Locator(".spots-create-dialog")).ToHaveAttributeAsync("aria-modal", "true");

        // Unique section prefix per run — the database persists between test runs.
        var prefix = $"G{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000}";

        // Even with the circuit attached, the grid re-renders as it grows and a tab clicked
        // mid-render can flip back, taking the panel (and its inputs) away again. Retry the
        // whole tab + fill sequence until the live preview shows exactly the expected count —
        // visibility alone is not enough, because one of the two fills can be lost while the
        // other lands. The preview assertion doubles as the wait, so there is no blind sleep.
        var countChip = Page.Locator(".batch-preview .count-chip");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await Page.Locator("fluent-tab", new() { HasText = "Generátor řady" }).ClickAsync(new() { Timeout = 5000 });
                await Page.Locator("fluent-text-field#ser-sections input").FillAsync(prefix, new() { Timeout = 5000 });
                await Page.Locator("fluent-number-field#ser-to input").FillAsync("5", new() { Timeout = 5000 });
                await Expect(countChip).ToHaveTextAsync("5", new() { Timeout = 3000 });
                break;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // The panel flipped away or a fill was lost mid-attempt — go around again.
                // (Click/fill timeouts surface as System.TimeoutException, which does NOT derive
                // from PlaywrightException — catching only the latter made this retry loop inert
                // and the spec flaky.)
            }
        }

        // The live preview proves the plan landed (and the create button is enabled).
        await Expect(countChip).ToHaveTextAsync("5");
        await Page.Locator("#ser-create").ClickAsync();

        await Expect(Page.GetByText(new Regex("Vytvořeno míst: 5|Created 5"))).ToBeVisibleAsync();
        await Expect(Page.Locator(".admin-panel")).ToContainTextAsync($"{prefix}-1");

        // Idempotence: the refreshed plan reports the whole series as already existing.
        await Expect(Page.GetByText(new Regex(@"Už existuje \(5\)|Already exists \(5\)"))).ToBeVisibleAsync();
        await Expect(Page.Locator(".batch-preview .count-chip")).ToHaveTextAsync("0");
    }

    [Test]
    public async Task Spot_type_changes_inline_from_the_grid()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/spots");
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Přidat místa|Add spots") }).ClickAsync();

        // Create a throwaway spot to retype. Even attached, a grid re-render can still swallow
        // an early click — retry until the new row appears, using the row itself as the wait.
        // FluentDataGrid renders a native table, so rows are plain <tr> elements.
        var code = $"T{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000}";
        var row = Page.Locator(".admin-panel tr", new() { HasText = code });
        for (var attempt = 0; attempt < 4 && !await row.IsVisibleAsync(); attempt++)
        {
            await Page.Locator("fluent-text-field#single-code input").FillAsync(code);
            await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^(Přidat|Add)$") }).ClickAsync();
            try
            {
                await row.WaitForAsync(new() { Timeout = 3000 });
            }
            catch (TimeoutException)
            {
                // The click was lost — go around again.
            }
        }

        await Expect(row).ToBeVisibleAsync();

        // Flip the type via the inline select in the type column (first select of the row).
        // Options carry localized labels; the CSS modifier class stays enum-based.
        await row.Locator("fluent-select").First.ClickAsync();
        await row.Locator("fluent-option", new() { HasText = "Pro držitele ZTP" }).ClickAsync();
        await Expect(row.Locator(".type-pill--Disabled")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Fleet_page_creates_a_vehicle_and_lists_it()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/fleet");
        await Expect(Page.Locator(".fleet-registry")).ToBeVisibleAsync();
        await Expect(Page.Locator("a[href='admin/parking/fleet']")).ToBeVisibleAsync();
        await Page.Locator("#fleet-open-add").ClickAsync();
        await Expect(Page.Locator(".fleet-editor")).ToBeVisibleAsync();
        await Expect(Page.Locator(".fleet-dialog-backdrop")).ToBeVisibleAsync();
        await Expect(Page.Locator(".fleet-editor")).ToHaveAttributeAsync("role", "dialog");
        await Expect(Page.Locator(".fleet-editor")).ToHaveAttributeAsync("aria-modal", "true");
        await Expect(Page.Locator(".fleet-workspace")).Not.ToHaveClassAsync(new Regex("has-side"));

        // Unique plate per run — the database persists between test runs. Same retry idiom as
        // the spots grid: a click landing mid-render can be swallowed, the row is the wait.
        var plate = $"E2E{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1_000_000}";
        var row = Page.Locator(".admin-panel tr", new() { HasText = plate });
        for (var attempt = 0; attempt < 4 && !await row.IsVisibleAsync(); attempt++)
        {
            await Page.Locator("fluent-text-field#fleet-plate input").FillAsync(plate);
            await Page.Locator("#fleet-add").ClickAsync();
            try
            {
                await row.WaitForAsync(new() { Timeout = 3000 });
            }
            catch (TimeoutException)
            {
                // The click was lost — go around again.
            }
        }

        await Expect(row).ToBeVisibleAsync();
        // No driver email was filled in, so the funnel column reads "manual pairing only".
        await Expect(row.Locator(".state-pill")).ToHaveTextAsync("Jen ruční párování");

        // Editing an existing record uses the same modal shell as creation, not the detail sidebar.
        await row.Locator(".fleet-vehicle-link").ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Upravit$") }).ClickAsync();
        await Expect(Page.Locator(".fleet-dialog-backdrop")).ToBeVisibleAsync();
        await Expect(Page.Locator("#fleet-save")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Zrušit$") }).ClickAsync();
        await Expect(Page.Locator(".fleet-dialog-backdrop")).ToHaveCountAsync(0);

        // Pairing is a separate focused decision, so it opens in the same dialog pattern instead
        // of expanding a form inside the vehicle detail sidebar.
        await row.Locator(".fleet-vehicle-link").ClickAsync();
        await Page.Locator("#fleet-open-pair").ClickAsync();
        await Expect(Page.Locator(".fleet-pairing-backdrop")).ToBeVisibleAsync();
        await Expect(Page.Locator(".fleet-pairing-dialog")).ToHaveAttributeAsync("role", "dialog");
        await Expect(Page.Locator(".fleet-pairing-dialog")).ToHaveAttributeAsync("aria-modal", "true");
        await Page.Locator(".fleet-pairing-dialog").GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Zrušit$|^Cancel$") }).ClickAsync();
        await Expect(Page.Locator(".fleet-pairing-backdrop")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Visitor_booking_opens_in_the_shared_admin_dialog()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/visitors");
        await Page.Locator("#visitors-open-create").ClickAsync();

        var dialog = Page.Locator(".visitors-create-dialog");
        await Expect(Page.Locator(".visitors-create-backdrop")).ToBeVisibleAsync();
        await Expect(dialog).ToHaveAttributeAsync("role", "dialog");
        await Expect(dialog).ToHaveAttributeAsync("aria-modal", "true");
        await Expect(dialog.Locator("input[type='date']")).ToBeVisibleAsync();

        await dialog.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Zrušit$|^Cancel$") }).ClickAsync();
        await Expect(Page.Locator(".visitors-create-backdrop")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Rules_and_pricing_tabs_switch_content()
    {
        // A click that lands during the circuit handshake is silently dropped and the tab never
        // switches — wait for the circuit instead of guessing with a sleep.
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/settings");
        await Expect(Page.GetByText("Ekonomika rezervací")).ToBeVisibleAsync();
        await Page.Locator("fluent-tab", new() { HasText = "Důvěra a ochrana" }).ClickAsync();
        await Expect(Page.GetByText("Graf důvěry", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Saving_the_rules_and_pricing_form_confirms_success()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/settings");
        await Expect(Page.GetByText("Ekonomika rezervací")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Uložit nastavení") }).ClickAsync();
        await Expect(Page.GetByText(new Regex("uloženo|saved", RegexOptions.IgnoreCase))).ToBeVisibleAsync();
    }

    [Test]
    public async Task Planner_reveals_only_the_controls_needed_by_each_choice()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/settings");
        await Expect(Page.GetByText("Typ rezervace", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator(".planner-summary")).ToHaveCountAsync(0);
        await Expect(Page.Locator(".reservation-mode-options")).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Switch, new() { NameString = "Používat přesný čas od–do" }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Switch, new() { NameString = "Povolit rezervace na dnešní den" }))
            .ToBeVisibleAsync();
        await Expect(Page.Locator(".planner-panel .settings-section__title")).ToHaveCountAsync(6);
        await Expect(Page.Locator("#planner-horizon option[value='365']"))
            .ToHaveTextAsync("365 dní dopředu");
        await Expect(Page.Locator(".planner-setting-note--horizon"))
            .ToContainTextAsync(new Regex(@"Poslední rezervovatelný den: \d{1,2}\.\s?\d{1,2}\.\s?\d{4} včetně\."));

        var horizonBox = await Page.Locator("#planner-horizon").BoundingBoxAsync();
        var weekdaysBox = await Page.Locator("#planner-weekdays").BoundingBoxAsync();
        var holidaysBox = await Page.GetByRole(AriaRole.Switch, new()
        {
            NameString = "Povolit rezervace o státních svátcích"
        }).BoundingBoxAsync();
        Assert.Multiple(() =>
        {
            Assert.That(horizonBox, Is.Not.Null);
            Assert.That(weekdaysBox, Is.Not.Null);
            Assert.That(holidaysBox, Is.Not.Null);
            Assert.That(weekdaysBox!.Y, Is.GreaterThan(horizonBox!.Y));
            Assert.That(holidaysBox!.Y, Is.GreaterThan(weekdaysBox.Y));
            Assert.That(Math.Abs(weekdaysBox.X - horizonBox.X), Is.LessThanOrEqualTo(1));
            Assert.That(Math.Abs(holidaysBox.X - horizonBox.X), Is.LessThanOrEqualTo(1));
        });

        await Page.Locator("#planner-horizon-help").FocusAsync();
        await Expect(Page.GetByText(new Regex("Určuje počet místních kalendářních dnů od dneška")))
            .ToBeVisibleAsync();

        await Expect(Page.GetByText("Upozornění na vytížení", new() { Exact = true })).ToBeVisibleAsync();
        var lowOccupancySwitch = Page.GetByRole(AriaRole.Switch, new()
        {
            NameString = "Upozornit při malé obsazenosti"
        });
        var highOccupancySwitch = Page.GetByRole(AriaRole.Switch, new()
        {
            NameString = "Upozornit při vysoké obsazenosti"
        });
        await Expect(lowOccupancySwitch).ToBeVisibleAsync();
        await Expect(highOccupancySwitch).ToBeVisibleAsync();
        if (!await Page.Locator("#planner-high-occupancy-threshold").IsVisibleAsync())
        {
            await highOccupancySwitch.ClickAsync();
        }
        await Expect(Page.Locator("#planner-high-occupancy-threshold")).ToBeVisibleAsync();
        await Page.Locator("#planner-high-occupancy-help").FocusAsync();
        await Expect(Page.GetByText(new Regex("držitele rezervací"))).ToBeVisibleAsync();

        await Page.Locator("fluent-tab", new() { HasText = "Provoz a přehled" }).ClickAsync();
        await Expect(Page.GetByText("Upozornění na vytížení", new() { Exact = true })).ToBeHiddenAsync();
        await Page.Locator("fluent-tab", new() { HasText = "Rezervace" }).ClickAsync();

        await Expect(Page.Locator(".weekday-picker")).ToBeHiddenAsync();
        await Page.Locator("#planner-weekdays").SelectOptionAsync("custom");
        await Expect(Page.Locator(".weekday-picker")).ToBeVisibleAsync();

        var weeklyLimitSwitch = Page.GetByRole(AriaRole.Switch, new() { NameString = "Použít týdenní limit" });
        if (await Page.Locator("#planner-weekly-limit").IsVisibleAsync())
        {
            await weeklyLimitSwitch.ClickAsync();
        }
        await Expect(Page.Locator("#planner-weekly-limit")).ToBeHiddenAsync();

        await weeklyLimitSwitch.ClickAsync();
        await Page.Locator("#planner-weekdays").SelectOptionAsync("everyday");
        await Page.Locator("#planner-weekly-limit input").FillAsync("7");
        await Page.Locator("#planner-weekdays").SelectOptionAsync("workdays");
        await Expect(Page.Locator("#planner-weekly-limit input")).ToHaveValueAsync("7");
        await Expect(Page.GetByText(new Regex("Nejvyšší možná hodnota podle povolených dnů")))
            .ToHaveCountAsync(0);
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Uložit nastavení") }).ClickAsync();
        await Expect(Page.GetByText(new Regex("Týdenní limit musí být alespoň 1"))).ToBeVisibleAsync();

        await Page.Locator("#planner-weekdays").SelectOptionAsync("everyday");
        await Expect(Page.Locator("#planner-weekly-limit input")).ToHaveValueAsync("7");
        await Expect(Page.Locator("#planner-weekly-limit")).ToHaveAttributeAsync("max", "7");

        var creditsSwitch = Page.GetByRole(AriaRole.Switch, new() { NameString = "Používat kredity" });
        if (!await Page.GetByText("Cena jedné rezervace (kredity)", new() { Exact = true }).IsVisibleAsync())
        {
            await creditsSwitch.ClickAsync();
        }
        await Expect(Page.GetByText("Cena jedné rezervace (kredity)", new() { Exact = true })).ToBeVisibleAsync();

        var priceBox = await Page.Locator("#planner-base-cost").BoundingBoxAsync();
        var allowanceBox = await Page.Locator("#planner-budget-allowance").BoundingBoxAsync();
        var renewalBox = await Page.Locator("#planner-budget-period").BoundingBoxAsync();
        Assert.Multiple(() =>
        {
            Assert.That(priceBox, Is.Not.Null);
            Assert.That(allowanceBox, Is.Not.Null);
            Assert.That(renewalBox, Is.Not.Null);
            Assert.That(allowanceBox!.Y, Is.GreaterThan(priceBox!.Y));
            Assert.That(renewalBox!.Y, Is.GreaterThan(allowanceBox.Y));
            Assert.That(Math.Abs(allowanceBox.X - priceBox.X), Is.LessThanOrEqualTo(1));
            Assert.That(Math.Abs(renewalBox.X - priceBox.X), Is.LessThanOrEqualTo(1));
        });

        await Page.Locator("#planner-base-cost input").FillAsync("20");
        await Page.Locator("#planner-budget-allowance input").FillAsync("10");
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Uložit nastavení") }).ClickAsync();
        await Expect(Page.GetByText(new Regex("Rozpočet na období musí pokrýt alespoň jednu rezervaci")))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Secondary_settings_tabs_follow_the_reservation_layout()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/settings");
        await Expect(Page.Locator("fluent-tab", new() { HasText = "Rezervace" })).ToBeVisibleAsync();

        var mapBox = await Page.Locator(".settings-map-disclosure").BoundingBoxAsync();
        var reservationPanelBox = await Page.Locator(".planner-panel").BoundingBoxAsync();
        var reservationTabBox = await Page.Locator("fluent-tab", new() { HasText = "Rezervace" }).BoundingBoxAsync();
        Assert.Multiple(() =>
        {
            Assert.That(mapBox, Is.Not.Null);
            Assert.That(reservationPanelBox, Is.Not.Null);
            Assert.That(reservationTabBox, Is.Not.Null);
            Assert.That(Math.Abs(mapBox!.X - reservationTabBox!.X), Is.LessThanOrEqualTo(1));
            Assert.That(Math.Abs(mapBox.Width - reservationPanelBox!.Width), Is.LessThanOrEqualTo(1));
        });

        await Page.Locator("fluent-tab", new() { HasText = "Provoz a přehled" }).ClickAsync();
        await Expect(Page.GetByText("Lhůty řešení", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator("#operations-sla-critical")).ToBeVisibleAsync();
        await Expect(Page.Locator("#operations-dispute-window")).ToBeVisibleAsync();
        await Page.Locator("#operations-sla-critical-help").FocusAsync();
        await Expect(Page.GetByText(new Regex("Nejdelší doba na vyřešení kritického"))).ToBeVisibleAsync();

        await Page.Locator("fluent-tab", new() { HasText = "Rezidenti" }).ClickAsync();
        await Expect(Page.Locator("#residents-plan-horizon")).ToBeVisibleAsync();
        await Expect(Page.Locator("#residents-alternative-policy")).ToBeVisibleAsync();
        await Expect(Page.Locator("#residents-reclaim-policy")).ToBeVisibleAsync();
        await Page.Locator("#residents-reclaim-policy-help").FocusAsync();
        await Expect(Page.GetByText(new Regex("kdy má rezident přednost před potvrzenou rezervací"))).ToBeVisibleAsync();
        await Expect(Page.GetByText(new Regex("Doporučení: Hybrid"))).Not.ToBeVisibleAsync();

        await Page.Locator("fluent-tab", new() { HasText = "Lokalita a limity" }).ClickAsync();
        await Expect(Page.Locator("#location-latitude")).ToBeVisibleAsync();
        await Expect(Page.Locator("#location-max-range")).ToBeVisibleAsync();

        await Page.Locator("fluent-tab", new() { HasText = "Notifikace" }).ClickAsync();
        await Expect(Page.Locator(".notification-rule-table").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Oversight_is_one_queue_of_cases_with_saved_views()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/oversight");

        // The two review queues are one list now; what used to be tabs are views over it.
        foreach (var view in new[] { "Otevřené", "Moje", "Nepřiřazené", "Vše" })
        {
            await Expect(Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(view) })).ToBeVisibleAsync();
        }

        // The shared database persists between runs, so rows may legitimately exist: assert the
        // queue renders one of its two states (empty state or a grid), not emptiness.
        await Expect(Page.Locator(".empty-state").Or(Page.Locator(".admin-panel")).First).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_retired_queue_routes_still_open_the_oversight_desk()
    {
        // Bookmarks and older deep links point at the pages the two queues used to have to
        // themselves; both now land on the merged desk rather than on a 404.
        foreach (var retired in new[] { "/admin/parking/collusion", "/admin/parking/mismatches" })
        {
            await Pages.GotoInteractiveAsync(Page, retired);
            await Expect(Page.GetByText(new Regex("Jedna fronta případů"))).ToBeVisibleAsync();
            await Expect(Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Otevřené") })).ToBeVisibleAsync();
        }
    }

    [Test]
    public async Task Switching_a_view_moves_the_address_bar_onto_it()
    {
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/oversight");
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Nepřiřazené") }).ClickAsync();

        // The URL is what makes a view linkable, and aria-pressed is what says which toggle is on.
        await Expect(Page).ToHaveURLAsync(new Regex("/admin/parking/oversight/unassigned$"));
        await Expect(Page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("Nepřiřazené") }))
            .ToHaveAttributeAsync("aria-pressed", "true");
    }
}
