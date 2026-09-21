using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Identity;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Web.Identity;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Exercises the production SCIM routes over HTTP, real Identity stores and the real directory
/// service against an isolated SQL Server database. Only settings and time are fixture inputs;
/// no request contacts Entra. Missing SQL configuration explicitly skips these integration tests.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ScimEndpointTests
{
    private WebApplication? _app;
    private HttpClient _client = null!;
    private DbContextOptions<D3ParkingDbContext>? _options;
    private string _bearer = null!;
    private EntraIdOptions _entra = null!;
    private FailAfterVisitorAuditSave _failure = null!;
    private readonly DateTimeOffset _now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    [SetUp]
    public async Task StartAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
            Assert.Ignore("ConnectionStrings__SqlServer is not set; SCIM HTTP tests require real SQL Server.");

        // Never select or delete the caller's catalog. Each test owns only its newly named catalog.
        var connection = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_ScimEndpointTests_{Guid.NewGuid():N}",
        };
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(connection.ConnectionString).Options;
        await using (var db = new D3ParkingDbContext(_options))
            await db.Database.EnsureCreatedAsync();

        _bearer = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _entra = new EntraIdOptions
        {
            Enabled = true,
            TenantId = "synthetic-test-tenant",
            ClientId = "synthetic-test-client",
            Scim = new ScimOptions { Enabled = true, BearerToken = _bearer },
        };
        _failure = new FailAfterVisitorAuditSave();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(ScimEndpointTests).Assembly.FullName,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<D3ParkingDbContext>(options => options
            .UseSqlServer(connection.ConnectionString).AddInterceptors(_failure));
        builder.Services.AddIdentityCore<ApplicationUser>(options => options.User.RequireUniqueEmail = true)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<D3ParkingDbContext>();
        builder.Services.AddSingleton<IEntraSettingsService>(new FixedEntraSettings(_entra));
        builder.Services.AddSingleton<IStringLocalizer<AccountMessages>, PassthroughLocalizer<AccountMessages>>();
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(_now));
        builder.Services.AddScoped<IExternalDirectoryService, EntraDirectoryService>();
        _app = builder.Build();
        _app.MapScimApi();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };

        await using var scope = _app.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var name in new[] { Roles.Administrator, Roles.Employee })
            Assert.That((await roles.CreateAsync(new ApplicationRole(name))).Succeeded, Is.True);
        var dbSeed = scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>();
        dbSeed.ExternalRoleMappings.Add(new ExternalRoleMapping(ExternalProviders.EntraId,
            "Parking.Employee", (await roles.FindByNameAsync(Roles.Employee))!.Id));
        await dbSeed.SaveChangesAsync();
    }

    [TearDown]
    public async Task StopAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
            _app = null;
        }
        if (_options is not null)
        {
            await using var db = new D3ParkingDbContext(_options);
            await db.Database.EnsureDeletedAsync();
            _options = null;
        }
    }

    [TestCase("PUT")]
    [TestCase("POST")]
    [TestCase("PATCH")]
    [TestCase("DELETE")]
    public async Task Last_administrator_departure_returns_scim_conflict_and_changes_nothing(string method)
    {
        var user = await CreateUserAsync("sole-admin", administrator: true);
        await SeedVisitsAsync(user.Id);
        var before = await SnapshotAsync();

        var path = method == "POST" ? "/scim/v2/Users" : $"/scim/v2/Users/{user.Id}";
        using var response = await SendAsync(new HttpMethod(method), path,
            method switch
            {
                "PUT" or "POST" => Replacement(user, false),
                "PATCH" => ActivePatch(false),
                _ => null,
            });

        await AssertScimErrorAsync(response, HttpStatusCode.Conflict, "Error_LastAdministrator");
        Assert.That(await SnapshotAsync(), Is.EqualTo(before),
            "A refused departure must preserve profile, account status, sessions, roles, visits and audit together.");
    }

    [TestCase("PUT", true)]
    [TestCase("PUT", false)]
    [TestCase("POST", true)]
    [TestCase("POST", false)]
    public async Task Replacement_departure_cancels_live_hosted_and_created_visits_once_and_keeps_history(
        string method, bool legacyBlockOnDeprovision)
    {
        _entra.Scim.BlockOnDeprovision = legacyBlockOnDeprovision;
        var user = await CreateUserAsync("leaver");
        var visits = await SeedVisitsAsync(user.Id);
        var stamp = user.SecurityStamp;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Resolve by the immutable external id as well as by the local account id.
            var id = attempt == 0 ? user.Id.ToString() : user.ExternalObjectId;
            var path = method == "POST" ? "/scim/v2/Users" : $"/scim/v2/Users/{id}";
            using var response = await SendAsync(new HttpMethod(method), path, Replacement(user, false));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/scim+json"));
            using var result = await ReadJsonAsync(response);
            Assert.Multiple(() =>
            {
                Assert.That(result.RootElement.GetProperty("id").GetString(), Is.EqualTo(user.Id.ToString()));
                Assert.That(result.RootElement.GetProperty("active").GetBoolean(), Is.False);
                Assert.That(result.RootElement.GetProperty("displayName").GetString(), Is.EqualTo("Synthetic replacement"));
            });
        }

        await using var db = new D3ParkingDbContext(_options!);
        var changed = await db.Users.SingleAsync(u => u.Id == user.Id);
        var all = await db.VisitorBookings.ToDictionaryAsync(v => v.Id);
        var audit = await db.AccountAuditEvents.Where(a => a.UserId == user.Id
            && a.Type == AccountAuditEventType.ReservationOverridden).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(changed.Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(changed.SecurityStamp, Is.Not.EqualTo(stamp));
            Assert.That(changed.Email, Is.EqualTo($"replacement-{user.Id:N}@example.test"));
            Assert.That(changed.Department, Is.EqualTo("Synthetic replacement department"));
            Assert.That(db.UserRoles.Any(r => r.UserId == user.Id), Is.False);
            Assert.That(db.ExternalRoleAssignments.Any(r => r.UserId == user.Id), Is.False);
            foreach (var id in visits.Live)
            {
                Assert.That(all[id].Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
                Assert.That(all[id].HostUserId, Is.Null);
                Assert.That(audit.Count(a => a.Detail!.Contains(id.ToString())), Is.EqualTo(1));
            }
            foreach (var id in visits.Finished)
            {
                Assert.That(all[id].Status, Is.EqualTo(VisitorBookingStatus.Booked));
                Assert.That(all[id].HostUserId, Is.EqualTo(user.Id));
            }
            Assert.That(all[visits.Unrelated].Status, Is.EqualTo(VisitorBookingStatus.Booked));
            Assert.That(all[visits.AlreadyCancelled].Status, Is.EqualTo(VisitorBookingStatus.Cancelled));
            Assert.That(audit, Has.Count.EqualTo(visits.Live.Length));
            Assert.That(audit.All(a => a.Actor == "system" && a.Detail is not null
                && a.Detail.Contains("employee departure") && !a.Detail.Contains("Synthetic")), Is.True);
            Assert.That(db.AccountAuditEvents.Count(a => a.UserId == user.Id
                && a.Type == AccountAuditEventType.Blocked), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Put_failure_after_visitor_audit_rolls_back_the_entire_replacement_then_retry_succeeds()
    {
        var user = await CreateUserAsync("rollback");
        await SeedVisitsAsync(user.Id);
        var before = await SnapshotAsync();
        _failure.Armed = true;

        using (var failed = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{user.Id}", Replacement(user, false)))
            Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(_failure.Armed, Is.False, "The injected failure must have reached the persisted visitor audit.");
        Assert.That(await SnapshotAsync(), Is.EqualTo(before),
            "A failure after a write must not leave the new profile, departure or partial parking cleanup committed.");

        using var retry = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{user.Id}", Replacement(user, false));
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(db.Users.Single(u => u.Id == user.Id).Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(db.AccountAuditEvents.Count(a => a.UserId == user.Id
                && a.Type == AccountAuditEventType.ReservationOverridden), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Put_with_explicit_active_true_reactivates_without_restoring_cancelled_visits_or_revoked_roles()
    {
        var user = await CreateUserAsync("returner");
        var visits = await SeedVisitsAsync(user.Id);
        using (var departure = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{user.Id}", ActivePatch(false)))
            Assert.That(departure.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{user.Id}", Replacement(user, true));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var result = await ReadJsonAsync(response);
            Assert.That(result.RootElement.GetProperty("active").GetBoolean(), Is.True);
        }

        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(db.Users.Single(u => u.Id == user.Id).Status, Is.EqualTo(AccountStatus.Active));
            Assert.That(db.UserRoles.Any(r => r.UserId == user.Id), Is.False);
            Assert.That(db.ExternalRoleAssignments.Any(r => r.UserId == user.Id), Is.False);
            Assert.That(db.VisitorBookings.Count(v => visits.Live.Contains(v.Id)
                && v.Status == VisitorBookingStatus.Cancelled), Is.EqualTo(visits.Live.Length));
            Assert.That(db.AccountAuditEvents.Count(a => a.UserId == user.Id
                && a.Type == AccountAuditEventType.Unblocked), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Put_without_active_changes_profile_without_implicitly_reactivating_a_blocked_account()
    {
        var user = await CreateUserAsync("attribute-only");
        using (var departure = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{user.Id}", ActivePatch(false)))
            Assert.That(departure.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var replacement = Replacement(user, null);

        using var response = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{user.Id}", replacement);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var result = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(result.RootElement.GetProperty("active").GetBoolean(), Is.False);
            Assert.That(result.RootElement.GetProperty("displayName").GetString(), Is.EqualTo("Synthetic replacement"));
        });
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(db.AccountAuditEvents.Any(a => a.UserId == user.Id && a.Type == AccountAuditEventType.Unblocked), Is.False);
    }

    [Test]
    public async Task Put_keeps_the_route_account_identity_when_payload_substitutes_another_external_id()
    {
        var target = await CreateUserAsync("route-target");
        var other = await CreateUserAsync("payload-target");
        var replacement = Replacement(target, false);
        replacement["externalId"] = other.ExternalObjectId!;

        using var response = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{target.Id}", replacement);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var db = new D3ParkingDbContext(_options!);
        var reloadedTarget = await db.Users.SingleAsync(u => u.Id == target.Id);
        var reloadedOther = await db.Users.SingleAsync(u => u.Id == other.Id);
        Assert.Multiple(() =>
        {
            Assert.That(reloadedTarget.ExternalObjectId, Is.EqualTo(target.ExternalObjectId));
            Assert.That(reloadedTarget.Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(reloadedOther.Status, Is.EqualTo(AccountStatus.Active));
            Assert.That(reloadedOther.Email, Is.EqualTo(other.Email));
            Assert.That(reloadedOther.DisplayName, Is.EqualTo(other.DisplayName));
            Assert.That(db.Users.Count(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Put_cannot_redirect_an_unlinked_route_account_to_an_existing_directory_account()
    {
        var other = await CreateUserAsync("directory-target");
        ApplicationUser local;
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            local = new ApplicationUser
            {
                UserName = "local-route@example.test",
                Email = "local-route@example.test",
                EmailConfirmed = true,
                DisplayName = "Synthetic local route",
                Status = AccountStatus.Active,
            };
            var result = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(local);
            Assert.That(result.Succeeded, Is.True);
        }
        var before = await SnapshotAsync();

        using var response = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{local.Id}", Replacement(other, false));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/scim+json"));
        Assert.That(await SnapshotAsync(), Is.EqualTo(before),
            "A valid SCIM token still cannot change a different account than the resource in the request URL.");
    }

    [Test]
    public async Task Put_can_adopt_the_verified_local_account_addressed_by_its_route()
    {
        ApplicationUser local;
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            local = new ApplicationUser
            {
                UserName = "local-adoption@example.test",
                Email = "local-adoption@example.test",
                EmailConfirmed = true,
                DisplayName = "Synthetic local account",
                Status = AccountStatus.Active,
            };
            var result = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(local);
            Assert.That(result.Succeeded, Is.True);
        }
        var payload = new
        {
            externalId = "scim-legitimate-adoption",
            userName = local.Email,
            displayName = "Synthetic adopted account",
            active = true,
        };

        using var response = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{local.Id}", payload);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var resultJson = await ReadJsonAsync(response);
        Assert.That(resultJson.RootElement.GetProperty("id").GetString(), Is.EqualTo(local.Id.ToString()));
        await using var db = new D3ParkingDbContext(_options!);
        var adopted = await db.Users.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(adopted.Id, Is.EqualTo(local.Id));
            Assert.That(adopted.ExternalProvider, Is.EqualTo(ExternalProviders.EntraId));
            Assert.That(adopted.ExternalObjectId, Is.EqualTo(payload.externalId));
            Assert.That(adopted.DisplayName, Is.EqualTo(payload.displayName));
            Assert.That(adopted.Status, Is.EqualTo(AccountStatus.Active));
            Assert.That(db.AccountAuditEvents.Count(a => a.UserId == local.Id
                && a.Type == AccountAuditEventType.Registered && a.Detail == "linked to EntraId"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Local_resource_id_takes_precedence_over_another_accounts_colliding_external_id()
    {
        // Create the competing external identity first so an unordered OR query cannot accidentally
        // prove the intended local-resource precedence through insertion order.
        var other = await CreateUserAsync("collision-other");
        var target = await CreateUserAsync("collision-local");
        await using (var seed = new D3ParkingDbContext(_options!))
        {
            await seed.Users.Where(u => u.Id == other.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ExternalObjectId, target.Id.ToString()));
        }

        using var response = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{target.Id}", ActivePatch(false));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var result = await ReadJsonAsync(response);
        Assert.That(result.RootElement.GetProperty("id").GetString(), Is.EqualTo(target.Id.ToString()));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(db.Users.Single(u => u.Id == target.Id).Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(db.Users.Single(u => u.Id == other.Id).Status, Is.EqualTo(AccountStatus.Active));
            Assert.That(db.AccountAuditEvents.Any(a => a.UserId == other.Id && a.Type == AccountAuditEventType.Blocked), Is.False);
        });
    }

    [Test]
    public async Task External_id_fallback_is_scoped_to_entra_when_another_provider_uses_the_same_id()
    {
        var other = await CreateUserAsync("provider-collision-other");
        var target = await CreateUserAsync("provider-collision-entra");
        await using (var seed = new D3ParkingDbContext(_options!))
        {
            await seed.Users.Where(u => u.Id == other.Id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.ExternalProvider, "SyntheticOtherProvider")
                .SetProperty(x => x.ExternalObjectId, target.ExternalObjectId));
        }

        using var response = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{target.ExternalObjectId}", ActivePatch(false));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var result = await ReadJsonAsync(response);
        Assert.That(result.RootElement.GetProperty("id").GetString(), Is.EqualTo(target.Id.ToString()));
        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(db.Users.Single(u => u.Id == target.Id).Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(db.Users.Single(u => u.Id == other.Id).Status, Is.EqualTo(AccountStatus.Active));
            Assert.That(db.AccountAuditEvents.Any(a => a.UserId == other.Id && a.Type == AccountAuditEventType.Blocked), Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task An_invalid_patch_operation_is_a_bad_request_without_changes(bool invalidActive)
    {
        var user = await CreateUserAsync("malformed-patch");
        var before = await SnapshotAsync();
        var payload = new { Operations = new object[]
        {
            invalidActive ? new { op = "Replace", path = "active", value = "invalid-boolean" } : 42,
        } };

        using var response = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{user.Id}", payload);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/scim+json"));
        Assert.That(await SnapshotAsync(), Is.EqualTo(before));
    }

    [TestCase("PATCH")]
    [TestCase("DELETE")]
    public async Task An_incomplete_directory_link_cannot_redirect_departure_to_another_account(string method)
    {
        var other = await CreateUserAsync("complete-directory-link");
        var target = await CreateUserAsync("incomplete-directory-link");
        await using (var seed = new D3ParkingDbContext(_options!))
        {
            await seed.Users.Where(u => u.Id == target.Id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.ExternalProvider, (string?)null)
                .SetProperty(x => x.ExternalObjectId, other.ExternalObjectId));
        }
        var before = await SnapshotAsync();

        using var response = await SendAsync(new HttpMethod(method), $"/scim/v2/Users/{target.Id}",
            method == "PATCH" ? ActivePatch(false) : null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await SnapshotAsync(), Is.EqualTo(before));
    }

    [TestCase("PUT")]
    [TestCase("PATCH")]
    [TestCase("DELETE")]
    public async Task An_account_bound_to_another_provider_cannot_be_mutated_through_entra_scim(string method)
    {
        var user = await CreateUserAsync("other-provider");
        await using (var db = new D3ParkingDbContext(_options!))
        {
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ExternalProvider, "SyntheticOtherProvider"));
        }
        var before = await SnapshotAsync();

        using var response = await SendAsync(new HttpMethod(method), $"/scim/v2/Users/{user.Id}", method switch
        {
            "PUT" => Replacement(user, false),
            "PATCH" => ActivePatch(false),
            _ => null,
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await SnapshotAsync(), Is.EqualTo(before));
    }

    [Test]
    public async Task Post_new_inactive_account_cannot_keep_baseline_roles()
    {
        _entra.DefaultRoles = [Roles.Employee];
        _entra.Scim.BlockOnDeprovision = false;
        var payload = new
        {
            externalId = "scim-new-inactive",
            userName = "new-inactive@example.test",
            displayName = "Synthetic inactive joiner",
            active = false,
        };

        using var response = await SendAsync(HttpMethod.Post, "/scim/v2/Users", payload);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        using var result = await ReadJsonAsync(response);
        var id = Guid.Parse(result.RootElement.GetProperty("id").GetString()!);
        Assert.That(result.RootElement.GetProperty("active").GetBoolean(), Is.False);
        await using var db = new D3ParkingDbContext(_options!);
        Assert.Multiple(() =>
        {
            Assert.That(db.Users.Single(u => u.Id == id).Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(db.UserRoles.Any(r => r.UserId == id), Is.False);
            Assert.That(db.ExternalRoleAssignments.Any(r => r.UserId == id), Is.False);
            Assert.That(db.AccountAuditEvents.Count(a => a.UserId == id && a.Type == AccountAuditEventType.Registered), Is.EqualTo(1));
        });
    }

    [TestCase("PUT", "[]")]
    [TestCase("PUT", "null")]
    [TestCase("PUT", "\"invalid-body\"")]
    [TestCase("POST", "[]")]
    [TestCase("POST", "null")]
    public async Task Non_object_payload_is_a_bad_scim_request_without_data_changes(string method, string json)
    {
        var user = await CreateUserAsync("bad-payload");
        var before = await SnapshotAsync();
        using var payload = JsonDocument.Parse(json);
        var path = method == "POST" ? "/scim/v2/Users" : $"/scim/v2/Users/{user.Id}";

        using var response = await SendAsync(new HttpMethod(method), path, payload.RootElement);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/scim+json"));
        Assert.That(await SnapshotAsync(), Is.EqualTo(before));
    }

    [TestCase("PUT")]
    [TestCase("POST")]
    public async Task Invalid_active_is_rejected_instead_of_implicitly_enabling_an_account(string method)
    {
        var user = await CreateUserAsync("bad-active");
        var before = await SnapshotAsync();
        var payload = Replacement(user, null);
        payload["active"] = "not-a-boolean";
        var path = method == "POST" ? "/scim/v2/Users" : $"/scim/v2/Users/{user.Id}";

        using var response = await SendAsync(new HttpMethod(method), path, payload);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/scim+json"));
        Assert.That(await SnapshotAsync(), Is.EqualTo(before));
    }

    [TestCase("true", true)]
    [TestCase("false", false)]
    public async Task Put_accepts_stringified_boolean_active_with_the_same_lifecycle_rules(string active, bool expected)
    {
        var user = await CreateUserAsync("string-active");
        using (var departure = await SendAsync(HttpMethod.Patch, $"/scim/v2/Users/{user.Id}", ActivePatch(false)))
            Assert.That(departure.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var payload = Replacement(user, null);
        payload["active"] = active;

        using var response = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{user.Id}", payload);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var result = await ReadJsonAsync(response);
        Assert.That(result.RootElement.GetProperty("active").GetBoolean(), Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("wrong")]
    public async Task Missing_or_wrong_bearer_cannot_read_or_mutate_any_scim_route(string? credential)
    {
        var user = await CreateUserAsync("protected");
        await SeedVisitsAsync(user.Id);
        var before = await SnapshotAsync();
        var operations = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"/scim/v2/Users/{user.Id}", null),
            (HttpMethod.Get, "/scim/v2/Users?filter=" + Uri.EscapeDataString($"userName eq \"{user.Email}\""), null),
            (HttpMethod.Get, "/scim/v2/ServiceProviderConfig", null),
            (HttpMethod.Post, "/scim/v2/Users", Replacement(user, true)),
            (HttpMethod.Put, $"/scim/v2/Users/{user.Id}", Replacement(user, false)),
            (HttpMethod.Patch, $"/scim/v2/Users/{user.Id}", ActivePatch(false)),
            (HttpMethod.Delete, $"/scim/v2/Users/{user.Id}", null),
        };
        foreach (var (method, path, body) in operations)
        {
            using var response = await SendAsync(method, path, body, authorized: false, credential);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), $"{method} {path}");
            Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty,
                "An unauthenticated response must not expose SCIM users or provisioning configuration.");
        }
        Assert.That(await SnapshotAsync(), Is.EqualTo(before));
    }

    [Test]
    public async Task Disabled_scim_rejects_even_a_valid_token_without_changing_data()
    {
        var user = await CreateUserAsync("disabled");
        var before = await SnapshotAsync();
        _entra.Scim.Enabled = false;

        using var response = await SendAsync(HttpMethod.Put, $"/scim/v2/Users/{user.Id}", Replacement(user, false));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await SnapshotAsync(), Is.EqualTo(before));
    }

    private async Task<ApplicationUser> CreateUserAsync(string suffix, bool administrator = false)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<IExternalDirectoryService>();
        var result = await directory.SyncAsync(new ExternalIdentity(ExternalProviders.EntraId,
            $"scim-{suffix}", $"{suffix}@example.test", "synthetic-test-tenant", $"Synthetic {suffix}",
            "Synthetic original department", ["Parking.Employee"]));
        Assert.That(result.Succeeded, Is.True, string.Join("; ", result.Errors));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(result.UserId.ToString()))!;
        if (administrator)
            Assert.That((await users.AddToRoleAsync(user, Roles.Administrator)).Succeeded, Is.True);
        return user;
    }

    private async Task<SeededVisits> SeedVisitsAsync(Guid userId)
    {
        var other = await CreateUserAsync($"other-host-{Guid.NewGuid():N}");
        var futureHosted = Booking(userId, other.Id, _now.AddDays(1), _now.AddDays(1).AddHours(1));
        var futureCreated = Booking(other.Id, userId, _now.AddDays(2), _now.AddDays(2).AddHours(1));
        var running = Booking(userId, userId, _now.AddMinutes(-30), _now.AddMinutes(30));
        var past = Booking(userId, userId, _now.AddDays(-2), _now.AddDays(-2).AddHours(1));
        var endingNow = Booking(userId, userId, _now.AddHours(-1), _now);
        var unrelated = Booking(other.Id, other.Id, _now.AddDays(3), _now.AddDays(3).AddHours(1));
        var cancelled = Booking(userId, userId, _now.AddDays(4), _now.AddDays(4).AddHours(1));
        cancelled.Cancel();
        await using var db = new D3ParkingDbContext(_options!);
        db.VisitorBookings.AddRange(futureHosted, futureCreated, running, past, endingNow, unrelated, cancelled);
        await db.SaveChangesAsync();
        return new SeededVisits([futureHosted.Id, futureCreated.Id, running.Id], [past.Id, endingNow.Id],
            unrelated.Id, cancelled.Id);
    }

    private VisitorBooking Booking(Guid hostId, Guid creatorId, DateTimeOffset start, DateTimeOffset end) =>
        new(Guid.NewGuid(), "Synthetic SCIM visitor", "Synthetic company", null, hostId,
            start, end, creatorId, _now.AddDays(-3));

    private static Dictionary<string, object> Replacement(ApplicationUser user, bool? active)
    {
        var payload = new Dictionary<string, object>
        {
            ["schemas"] = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
            ["externalId"] = user.ExternalObjectId!,
            ["userName"] = $"replacement-{user.Id:N}@example.test",
            ["displayName"] = "Synthetic replacement",
            ["department"] = "Synthetic replacement department",
        };
        if (active.HasValue) payload["active"] = active.Value;
        return payload;
    }

    private static object ActivePatch(bool active) => new
    {
        schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" },
        Operations = new[] { new { op = "Replace", path = "active", value = active } },
    };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? payload = null,
        bool authorized = true, string? credential = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (authorized || credential is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorized ? _bearer : credential);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, mediaType: new MediaTypeHeaderValue("application/scim+json"));
        return await _client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task AssertScimErrorAsync(HttpResponseMessage response, HttpStatusCode expected, string detail)
    {
        Assert.That(response.StatusCode, Is.EqualTo(expected));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/scim+json"));
        using var result = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(result.RootElement.GetProperty("schemas")[0].GetString(),
                Is.EqualTo("urn:ietf:params:scim:api:messages:2.0:Error"));
            Assert.That(result.RootElement.GetProperty("status").GetString(), Is.EqualTo(((int)expected).ToString()));
            Assert.That(result.RootElement.GetProperty("detail").GetString(), Does.Contain(detail));
        });
    }

    private async Task<string> SnapshotAsync()
    {
        await using var db = new D3ParkingDbContext(_options!);
        // All records are synthetic. Include normalized identity, session stamps and role provenance
        // so a failed request cannot pass merely because its public status did not change.
        return JsonSerializer.Serialize(new
        {
            Users = await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => new
            {
                u.Id, u.UserName, u.NormalizedUserName, u.Email, u.NormalizedEmail, u.EmailConfirmed,
                u.DisplayName, u.Department, u.Status, u.StatusReason, u.StatusChangedAtUtc,
                u.SecurityStamp, u.ConcurrencyStamp, u.ExternalProvider, u.ExternalObjectId,
                u.ExternalTenantId, u.ExternalSyncedAtUtc,
            }).ToArrayAsync(),
            Roles = await db.UserRoles.AsNoTracking().OrderBy(r => r.UserId).ThenBy(r => r.RoleId).ToArrayAsync(),
            ExternalRoles = await db.ExternalRoleAssignments.AsNoTracking()
                .OrderBy(r => r.UserId).ThenBy(r => r.RoleId).ToArrayAsync(),
            Visits = await db.VisitorBookings.AsNoTracking().OrderBy(v => v.Id).ToArrayAsync(),
            Audit = await db.AccountAuditEvents.AsNoTracking().OrderBy(a => a.Id).ToArrayAsync(),
        });
    }

    private sealed record SeededVisits(Guid[] Live, Guid[] Finished, Guid Unrelated, Guid AlreadyCancelled);

    private sealed class FailAfterVisitorAuditSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<AccountAuditEvent>()
                .Any(e => e.Entity.Type == AccountAuditEventType.ReservationOverridden
                    && e.Entity.Detail != null && e.Entity.Detail.Contains("employee departure")))
            {
                Armed = false;
                throw new InvalidOperationException("Synthetic SCIM failure after visitor audit save.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedEntraSettings(EntraIdOptions options) : IEntraSettingsService
    {
        public Task<EntraIdOptions> GetEffectiveAsync(CancellationToken cancellationToken = default) => Task.FromResult(options);
        public EntraIdOptions GetEffective() => options;
        public Task<EntraSettingsView> GetForAdminAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Accounts.AccountResult> UpdateAsync(EntraSettingsUpdate update, Guid actingUserId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
