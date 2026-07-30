using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using D3Parking.Application.Identity;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Settings;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Covers the two rules that make the Entra settings page trustworthy: the deployment's
/// configuration wins field by field over what an administrator stored, and a secret that goes into
/// the database never comes back out of it in the clear or by accident.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class EntraSettingsTests
{
    private const string Secret = "a-client-secret-value";
    private const string ScimToken = "0123456789abcdef0123456789abcdef0123456789";

    private ServiceProvider _provider = null!;
    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private IDbContextFactory<D3ParkingDbContext> _factory = null!;
    private IDataProtectionProvider _dataProtection = null!;
    private readonly Guid _admin = Guid.NewGuid();

    [SetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the settings tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_EntraSettingsTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContextFactory<D3ParkingDbContext>(o => o.UseSqlServer(builder.ConnectionString));
        _provider = services.BuildServiceProvider();

        _factory = _provider.GetRequiredService<IDbContextFactory<D3ParkingDbContext>>();
        _dataProtection = _provider.GetRequiredService<IDataProtectionProvider>();

        // Default roles are validated against the role table, so one has to exist to be chosen.
        await using var seed = await _factory.CreateDbContextAsync();
        seed.Roles.Add(new ApplicationRole(Roles.Employee));
        seed.Roles.Add(new ApplicationRole(Roles.Administrator));
        await seed.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        if (_options is not null)
        {
            await using var dbContext = new D3ParkingDbContext(_options);
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task Configuration_wins_over_the_stored_value_and_locks_the_field()
    {
        var stored = CreateService();
        var saved = await stored.UpdateAsync(Update(Settings() with { TenantId = "stored-tenant" }), _admin);
        Assert.That(saved.Succeeded, Is.True, string.Join("; ", saved.Errors));

        var pinned = CreateService(new() { ["EntraId:TenantId"] = "pinned-tenant" });

        var effective = await pinned.GetEffectiveAsync();
        Assert.That(effective.TenantId, Is.EqualTo("pinned-tenant"));

        var view = await pinned.GetForAdminAsync();
        Assert.Multiple(() =>
        {
            // The page shows what is in force and says it cannot be edited, rather than showing the
            // row underneath that nothing reads.
            Assert.That(view.Settings.TenantId, Is.EqualTo("pinned-tenant"));
            Assert.That(view.IsLocked(EntraFields.TenantId), Is.True);
            Assert.That(view.IsLocked(EntraFields.ClientId), Is.False);
        });
    }

    [Test]
    public async Task An_unset_configuration_key_leaves_the_stored_value_alone()
    {
        var service = CreateService();
        await service.UpdateAsync(Update(Settings() with { TenantId = "stored-tenant", DisplayName = "Work account" }), _admin);

        // An empty string is "present but says nothing"; treating it as set is what would freeze the
        // page for every installation shipping a documented but blank section.
        var withBlanks = CreateService(new() { ["EntraId:TenantId"] = "", ["EntraId:ClientId"] = "   " });

        var effective = await withBlanks.GetEffectiveAsync();
        Assert.Multiple(() =>
        {
            Assert.That(effective.TenantId, Is.EqualTo("stored-tenant"));
            Assert.That(effective.DisplayName, Is.EqualTo("Work account"));
        });

        var view = await withBlanks.GetForAdminAsync();
        Assert.That(view.LockedFields, Is.Empty);
    }

    [Test]
    public async Task A_configured_false_switch_still_overrides_a_stored_true()
    {
        var service = CreateService();
        var saved = await service.UpdateAsync(
            Update(Settings() with { Enabled = true, TenantId = "t", ClientId = "c" }, secret: EntraSecretUpdate.Set(Secret)),
            _admin);
        Assert.That(saved.Succeeded, Is.True, string.Join("; ", saved.Errors));

        var disabled = CreateService(new() { ["EntraId:Enabled"] = "false" });

        var effective = await disabled.GetEffectiveAsync();
        Assert.Multiple(() =>
        {
            Assert.That(effective.Enabled, Is.False);
            Assert.That(effective.IsSignInConfigured, Is.False);
        });
    }

    [Test]
    public async Task A_stored_secret_is_encrypted_at_rest_and_never_read_back_to_the_page()
    {
        var service = CreateService();
        var saved = await service.UpdateAsync(
            Update(Settings() with { Enabled = true, TenantId = "t", ClientId = "c" }, secret: EntraSecretUpdate.Set(Secret)),
            _admin);
        Assert.That(saved.Succeeded, Is.True, string.Join("; ", saved.Errors));

        await using var dbContext = await _factory.CreateDbContextAsync();
        var row = await dbContext.EntraSettings.AsNoTracking().SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(row.ClientSecretProtected, Is.Not.Null);
            Assert.That(row.ClientSecretProtected, Is.Not.EqualTo(Secret));
            Assert.That(row.ClientSecretProtected, Does.Not.Contain(Secret));
        });

        // The sign-in path needs the plaintext; the settings page is told only that one exists.
        Assert.That((await service.GetEffectiveAsync()).ClientSecret, Is.EqualTo(Secret));
        Assert.That((await service.GetForAdminAsync()).ClientSecretSet, Is.True);
    }

    [Test]
    public async Task An_untouched_secret_survives_an_unrelated_save_and_clearing_removes_it()
    {
        var service = CreateService();
        await service.UpdateAsync(
            Update(Settings() with { Enabled = true, TenantId = "t", ClientId = "c" }, secret: EntraSecretUpdate.Set(Secret)),
            _admin);

        // A blank secret box means "leave it alone" — the page never showed what was there.
        var kept = await service.UpdateAsync(
            Update(Settings() with { Enabled = true, TenantId = "t", ClientId = "c", DisplayName = "Work account" }),
            _admin);
        Assert.That(kept.Succeeded, Is.True, string.Join("; ", kept.Errors));
        Assert.That((await service.GetEffectiveAsync()).ClientSecret, Is.EqualTo(Secret));

        // Removing the secret means sign-in can no longer work, so it has to be switched off too.
        var cleared = await service.UpdateAsync(
            Update(Settings() with { Enabled = false }, secret: EntraSecretUpdate.Clear),
            _admin);
        Assert.That(cleared.Succeeded, Is.True, string.Join("; ", cleared.Errors));
        Assert.That((await service.GetForAdminAsync()).ClientSecretSet, Is.False);
    }

    [Test]
    public async Task Sign_in_cannot_be_switched_on_without_the_values_it_needs()
    {
        var service = CreateService();

        var noTenant = await service.UpdateAsync(Update(Settings() with { Enabled = true, ClientId = "c" }), _admin);
        Assert.That(noTenant.Succeeded, Is.False);

        var noSecret = await service.UpdateAsync(
            Update(Settings() with { Enabled = true, TenantId = "t", ClientId = "c" }), _admin);
        Assert.That(noSecret.Succeeded, Is.False);
    }

    [Test]
    public async Task Provisioning_cannot_be_switched_on_without_a_bearer_token()
    {
        var service = CreateService();

        var noToken = await service.UpdateAsync(Update(Settings() with { ScimEnabled = true }), _admin);
        Assert.That(noToken.Succeeded, Is.False);

        // The token is the entire boundary on those endpoints, so a hand-typed placeholder is
        // refused as well as an absent one.
        var weakToken = await service.UpdateAsync(
            Update(Settings() with { ScimEnabled = true }, scim: EntraSecretUpdate.Set("letmein")), _admin);
        Assert.That(weakToken.Succeeded, Is.False);

        var good = await service.UpdateAsync(
            Update(Settings() with { ScimEnabled = true }, scim: EntraSecretUpdate.Set(ScimToken)), _admin);
        Assert.That(good.Succeeded, Is.True, string.Join("; ", good.Errors));
        Assert.That((await service.GetEffectiveAsync()).Scim.IsConfigured, Is.True);
    }

    [Test]
    public async Task Administrator_is_refused_as_a_default_role()
    {
        var service = CreateService();

        // A default role is granted to every account the directory creates on first sign-in, so
        // this would make "anyone in the tenant" an administrator here.
        var escalation = await service.UpdateAsync(
            Update(Settings() with { DefaultRoles = [Roles.Administrator] }), _admin);
        Assert.That(escalation.Succeeded, Is.False);

        var unknown = await service.UpdateAsync(
            Update(Settings() with { DefaultRoles = ["NoSuchRole"] }), _admin);
        Assert.That(unknown.Succeeded, Is.False);

        var allowed = await service.UpdateAsync(
            Update(Settings() with { DefaultRoles = [Roles.Employee] }), _admin);
        Assert.That(allowed.Succeeded, Is.True, string.Join("; ", allowed.Errors));
    }

    [Test]
    public async Task A_secret_that_cannot_be_decrypted_is_ignored_rather_than_thrown()
    {
        var service = CreateService();
        await service.UpdateAsync(
            Update(Settings() with { Enabled = true, TenantId = "t", ClientId = "c" }, secret: EntraSecretUpdate.Set(Secret)),
            _admin);

        // What a restore onto a machine with a different data protection key ring looks like.
        await using (var dbContext = await _factory.CreateDbContextAsync())
        {
            var row = await dbContext.EntraSettings.SingleAsync();
            row.SetClientSecret("not-a-payload-this-protector-can-read");
            await dbContext.SaveChangesAsync();
        }

        var effective = await CreateService().GetEffectiveAsync();
        Assert.Multiple(() =>
        {
            // The application still starts and still says sign-in is on; the administrator is the
            // one who has to re-enter the secret, and the log says so.
            Assert.That(effective.ClientSecret, Is.Null);
            Assert.That(effective.TenantId, Is.EqualTo("t"));
        });
    }

    private EntraSettingsService CreateService(Dictionary<string, string?>? configuration = null) => new(
        _factory,
        // A cache per service, so a test that changes configuration reads it rather than a snapshot
        // the previous instance left behind.
        new MemoryCache(new MemoryCacheOptions()),
        new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build(),
        _dataProtection,
        new NullEntraRuntimeReloader(),
        new PassthroughLocalizer<AccountMessages>(),
        TimeProvider.System,
        NullLogger<EntraSettingsService>.Instance);

    private static EntraSettingsDto Settings() => new(
        Enabled: false,
        TenantId: null,
        ClientId: null,
        Authority: null,
        CallbackPath: "/signin-entra",
        SignedOutCallbackPath: "/signout-entra",
        DisplayName: "Microsoft",
        LinkByVerifiedEmail: true,
        AllowJustInTimeProvisioning: true,
        DefaultRoles: [],
        ScimEnabled: false,
        ScimBlockOnDeprovision: true);

    private static EntraSettingsUpdate Update(
        EntraSettingsDto settings,
        EntraSecretUpdate? secret = null,
        EntraSecretUpdate? scim = null) =>
        new(settings, secret ?? EntraSecretUpdate.Keep, scim ?? EntraSecretUpdate.Keep);
}
