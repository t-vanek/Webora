using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Identity;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Exercises the fleet pairing handshake against SQL Server with the real Identity token stack:
/// pairing needs the plate match, the registry's driver email equal to the account's email AND a
/// confirmed emailed code — and it materializes residency on the vehicle's assigned spot. The
/// guardrails (attempt lockout, resend throttle, opaque mismatch answers, single winner under
/// concurrency, no eviction of an existing resident) are what these tests pin down.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class FleetPairingTests
{
    private ServiceProvider _provider = null!;
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the fleet pairing tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_FleetPairingTests",
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
        services.AddDbContext<D3ParkingDbContext>(o => o.UseSqlServer(builder.ConnectionString));
        // Data protection backs the default token providers (the pairing code is an Rfc6238
        // email token); ephemeral keys are fine for a test process.
        services.AddDataProtection();
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<D3ParkingDbContext>()
            .AddDefaultTokenProviders();
        _provider = services.BuildServiceProvider();
    }

    [OneTimeTearDown]
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
    public async Task Matching_plate_email_and_code_pair_and_grant_residency()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fleet = CreateFleetService(scope);

        var user = await CreateUserAsync(userManager, "happy", plate: "1AB 2345");
        var spotId = await CreateSpotAsync("FL-01");
        Assert.That((await fleet.CreateAsync("1ab-2345", "Octavia", user.Email, spotId, null)).Succeeded, Is.True);

        var status = await fleet.GetMyPairingStatusAsync(user.Id);
        Assert.That(status.State, Is.EqualTo(VehiclePairingState.CodeRequired),
            "Matching plate + matching driver email must land on the code step, never pair silently.");

        Assert.That((await fleet.RequestPairingCodeAsync(user.Id)).Succeeded, Is.True);
        var throttled = await fleet.RequestPairingCodeAsync(user.Id);
        Assert.That(throttled.Errors, Does.Contain("Fleet_Error_CodeThrottled"),
            "An immediate resend must be throttled so the mailbox cannot be spammed.");

        // Rfc6238 codes are deterministic within their window, so generating with the same
        // purpose reproduces exactly what the email carried.
        var code = await GenerateCodeAsync(userManager, user);
        Assert.That((await fleet.ConfirmPairingAsync(user.Id, code)).Succeeded, Is.True);

        var vehicle = await LoadVehicleAsync("1AB2345");
        Assert.That(vehicle.PairedUserId, Is.EqualTo(user.Id));
        await using var dbContext = new D3ParkingDbContext(_options);
        var owner = await dbContext.ParkingSpots.Where(s => s.Id == spotId).Select(s => s.OwnerId).SingleAsync();
        Assert.That(owner, Is.EqualTo(user.Id), "Pairing must materialize residency on the vehicle's spot.");

        var paired = await fleet.GetMyPairingStatusAsync(user.Id);
        Assert.That(paired.State, Is.EqualTo(VehiclePairingState.Paired));
        Assert.That(paired.SpotCode, Is.EqualTo("FL-01"));
    }

    [Test]
    public async Task Five_wrong_codes_lock_the_vehicle_even_for_the_right_code()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fleet = CreateFleetService(scope);

        var user = await CreateUserAsync(userManager, "lockout", plate: "2CD 6789");
        Assert.That((await fleet.CreateAsync("2CD 6789", null, user.Email, null, null)).Succeeded, Is.True);
        Assert.That((await fleet.RequestPairingCodeAsync(user.Id)).Succeeded, Is.True);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var wrong = await fleet.ConfirmPairingAsync(user.Id, "000000");
            Assert.That(wrong.Errors, Does.Contain("Fleet_Error_CodeInvalid"));
        }

        var lockedOut = await fleet.ConfirmPairingAsync(user.Id, await GenerateCodeAsync(userManager, user));
        Assert.That(lockedOut.Errors, Does.Contain("Fleet_Error_TooManyAttempts"),
            "After five failures the 6-digit space must close, correct code or not.");
        Assert.That((await LoadVehicleAsync("2CD6789")).PairedUserId, Is.Null);
    }

    [Test]
    public async Task Email_mismatch_and_missing_email_answer_with_the_same_opaque_refusal()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fleet = CreateFleetService(scope);

        var user = await CreateUserAsync(userManager, "mismatch", plate: "3EF 1111");
        Assert.That((await fleet.CreateAsync("3EF 1111", null, "somebody.else@test.local", null, null)).Succeeded, Is.True);

        var status = await fleet.GetMyPairingStatusAsync(user.Id);
        Assert.That(status.State, Is.EqualTo(VehiclePairingState.NotPairable),
            "A matching plate with a foreign driver email must not be pairable.");
        var refused = await fleet.RequestPairingCodeAsync(user.Id);
        Assert.That(refused.Errors, Does.Contain("Fleet_Error_NotPairable"));

        // A pool car with no driver email must be indistinguishable from the mismatch above.
        var poolUser = await CreateUserAsync(userManager, "pool", plate: "4GH 2222");
        Assert.That((await fleet.CreateAsync("4GH 2222", null, null, null, null)).Succeeded, Is.True);
        var poolStatus = await fleet.GetMyPairingStatusAsync(poolUser.Id);
        Assert.That(poolStatus.State, Is.EqualTo(VehiclePairingState.NotPairable));
        var poolRefused = await fleet.RequestPairingCodeAsync(poolUser.Id);
        Assert.That(poolRefused.Errors, Does.Contain("Fleet_Error_NotPairable"));
    }

    [Test]
    public async Task Removing_the_profile_plate_unpairs_and_releases_the_residency()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fleet = CreateFleetService(scope);

        var user = await CreateUserAsync(userManager, "unpair", plate: "5IJ 3333");
        var spotId = await CreateSpotAsync("FL-02");
        Assert.That((await fleet.CreateAsync("5IJ 3333", null, user.Email, spotId, null)).Succeeded, Is.True);
        var vehicle = await LoadVehicleAsync("5IJ3333");
        Assert.That((await fleet.PairManuallyAsync(vehicle.Id, user.Id)).Succeeded, Is.True);

        user.LicensePlate = null;
        Assert.That((await userManager.UpdateAsync(user)).Succeeded, Is.True);
        await fleet.SyncUserPlateAsync(user.Id);

        Assert.That((await LoadVehicleAsync("5IJ3333")).PairedUserId, Is.Null,
            "The pairing exists because of the plate; removing the plate severs it.");
        await using var dbContext = new D3ParkingDbContext(_options);
        var owner = await dbContext.ParkingSpots.Where(s => s.Id == spotId).Select(s => s.OwnerId).SingleAsync();
        Assert.That(owner, Is.Null, "The residency the pairing carried goes back with the vehicle.");
    }

    [Test]
    public async Task Concurrent_claims_of_one_vehicle_pick_a_single_winner()
    {
        Guid userA, userB, spotId, vehicleId;
        await using (var seedScope = _provider.CreateAsyncScope())
        {
            var userManager = seedScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var fleet = CreateFleetService(seedScope);
            userA = (await CreateUserAsync(userManager, "race-a", plate: "6KL 4444")).Id;
            userB = (await CreateUserAsync(userManager, "race-b", plate: "6KL 4444")).Id;
            spotId = await CreateSpotAsync("FL-03");
            Assert.That((await fleet.CreateAsync("6KL 4444", null, null, spotId, null)).Succeeded, Is.True);
            vehicleId = (await LoadVehicleAsync("6KL4444")).Id;
        }

        // Two independent scopes = two contexts = a real race on the vehicle row.
        await using var scopeA = _provider.CreateAsyncScope();
        await using var scopeB = _provider.CreateAsyncScope();
        var results = await Task.WhenAll(
            CreateFleetService(scopeA).PairManuallyAsync(vehicleId, userA),
            CreateFleetService(scopeB).PairManuallyAsync(vehicleId, userB));

        Assert.That(results.Count(r => r.Succeeded), Is.EqualTo(1),
            "Exactly one concurrent claim of the same vehicle may win.");
        var vehicle = await LoadVehicleAsync("6KL4444");
        var winner = results[0].Succeeded ? userA : userB;
        Assert.That(vehicle.PairedUserId, Is.EqualTo(winner));
        await using var dbContext = new D3ParkingDbContext(_options);
        var owner = await dbContext.ParkingSpots.Where(s => s.Id == spotId).Select(s => s.OwnerId).SingleAsync();
        Assert.That(owner, Is.EqualTo(winner), "The residency must follow the winning pairing, never the loser.");
    }

    [Test]
    public async Task Registering_a_vehicle_for_an_existing_account_nudges_the_driver()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var sent = new RecordingNotificationService();
        var fleet = CreateFleetService(scope, sent);

        var user = await CreateUserAsync(userManager, "nudge", plate: "8OP 6666");
        Assert.That((await fleet.CreateAsync("8OP 6666", null, user.Email, null, null)).Succeeded, Is.True);
        Assert.That(sent.Sent, Does.Contain((user.Id, "Fleet_Notify_Pairable_Title")),
            "A car registered after the account already exists must nudge the driver; nobody re-opens their profile on a hunch.");

        // Cosmetic edits (name, notes, spot) must not re-nudge — only identity changes do.
        sent.Sent.Clear();
        var vehicle = await LoadVehicleAsync("8OP6666");
        Assert.That((await fleet.UpdateAsync(vehicle.Id, "8OP 6666", "Fabia", user.Email, null, "poznámka")).Succeeded, Is.True);
        Assert.That(sent.Sent, Is.Empty);
    }

    [Test]
    public async Task Correcting_the_driver_email_nudges_the_newly_matching_driver()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var sent = new RecordingNotificationService();
        var fleet = CreateFleetService(scope, sent);

        var user = await CreateUserAsync(userManager, "typo", plate: "9QR 7777");
        Assert.That((await fleet.CreateAsync("9QR 7777", null, $"ghost-{Guid.NewGuid():N}@test.local", null, null)).Succeeded, Is.True);
        Assert.That(sent.Sent, Is.Empty, "An email matching no account has nobody to nudge.");

        var vehicle = await LoadVehicleAsync("9QR7777");
        Assert.That((await fleet.UpdateAsync(vehicle.Id, "9QR 7777", null, user.Email, null, null)).Succeeded, Is.True);
        Assert.That(sent.Sent, Does.Contain((user.Id, "Fleet_Notify_Pairable_Title")),
            "Fixing the registry's email typo is exactly the moment the real driver becomes pairable.");
    }

    [Test]
    public async Task Reactivating_a_vehicle_nudges_the_driver_again()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var sent = new RecordingNotificationService();
        var fleet = CreateFleetService(scope, sent);

        var user = await CreateUserAsync(userManager, "revive", plate: "1SU 8888");
        Assert.That((await fleet.CreateAsync("1SU 8888", null, user.Email, null, null)).Succeeded, Is.True);
        var vehicle = await LoadVehicleAsync("1SU8888");

        sent.Sent.Clear();
        Assert.That((await fleet.SetActiveAsync(vehicle.Id, false)).Succeeded, Is.True);
        Assert.That((await fleet.SetActiveAsync(vehicle.Id, true)).Succeeded, Is.True);
        Assert.That(sent.Sent, Does.Contain((user.Id, "Fleet_Notify_Pairable_Title")),
            "A reactivated car is pairable again, so the driver hears about it like a fresh registration.");
    }

    [Test]
    public async Task No_nudge_for_a_driver_already_paired_with_another_vehicle()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var sent = new RecordingNotificationService();
        var fleet = CreateFleetService(scope, sent);

        var user = await CreateUserAsync(userManager, "busy", plate: "2VW 9999");
        Assert.That((await fleet.CreateAsync("3XY 0001", "Pool", null, null, null)).Succeeded, Is.True);
        Assert.That((await fleet.PairManuallyAsync((await LoadVehicleAsync("3XY0001")).Id, user.Id)).Succeeded, Is.True);

        sent.Sent.Clear();
        Assert.That((await fleet.CreateAsync("2VW 9999", null, user.Email, null, null)).Succeeded, Is.True);
        Assert.That(sent.Sent, Does.Not.Contain((user.Id, "Fleet_Notify_Pairable_Title")),
            "One account, one pairing: whoever is paired already must not be lured into a second code ceremony.");

        var status = await fleet.GetMyPairingStatusAsync(user.Id);
        Assert.That(status.State, Is.EqualTo(VehiclePairingState.Paired));
        Assert.That(status.VehiclePlate, Is.EqualTo("3XY 0001"),
            "The status reports the pairing the user actually has, not the plate coincidence.");
    }

    [Test]
    public async Task Manually_paired_pool_driver_sees_the_pairing_in_their_status()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fleet = CreateFleetService(scope);

        var user = await CreateUserAsync(userManager, "pooldriver", plate: null);
        var spotId = await CreateSpotAsync("FL-05");
        Assert.That((await fleet.CreateAsync("4ZA 0002", "Pool Transporter", null, spotId, null)).Succeeded, Is.True);
        Assert.That((await fleet.PairManuallyAsync((await LoadVehicleAsync("4ZA0002")).Id, user.Id)).Succeeded, Is.True);

        var status = await fleet.GetMyPairingStatusAsync(user.Id);
        Assert.That(status.State, Is.EqualTo(VehiclePairingState.Paired),
            "The pairing is the user's own state; no profile plate is needed to see it.");
        Assert.That(status.VehiclePlate, Is.EqualTo("4ZA 0002"));
        Assert.That(status.SpotCode, Is.EqualTo("FL-05"));
    }

    [Test]
    public async Task The_admin_list_names_each_vehicles_funnel_state()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fleet = CreateFleetService(scope);

        Assert.That((await fleet.CreateAsync("POOL 100", null, null, null, null)).Succeeded, Is.True);
        Assert.That((await fleet.CreateAsync("GHST 200", null, $"ghost-{Guid.NewGuid():N}@test.local", null, null)).Succeeded, Is.True);

        var plateless = await CreateUserAsync(userManager, "plateless", plate: null);
        Assert.That((await fleet.CreateAsync("PLAT 300", null, plateless.Email, null, null)).Succeeded, Is.True);

        var ready = await CreateUserAsync(userManager, "ready", plate: "REDY 400");
        Assert.That((await fleet.CreateAsync("REDY 400", null, ready.Email, null, null)).Succeeded, Is.True);

        var coded = await CreateUserAsync(userManager, "coded", plate: "CODE 500");
        Assert.That((await fleet.CreateAsync("CODE 500", null, coded.Email, null, null)).Succeeded, Is.True);
        Assert.That((await fleet.RequestPairingCodeAsync(coded.Id)).Succeeded, Is.True);

        var locked = await CreateUserAsync(userManager, "locked", plate: "LOCK 600");
        Assert.That((await fleet.CreateAsync("LOCK 600", null, locked.Email, null, null)).Succeeded, Is.True);
        Assert.That((await fleet.RequestPairingCodeAsync(locked.Id)).Succeeded, Is.True);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.That((await fleet.ConfirmPairingAsync(locked.Id, "000000")).Errors, Does.Contain("Fleet_Error_CodeInvalid"));
        }

        var paired = await CreateUserAsync(userManager, "funnelpair", plate: "PAIR 700");
        Assert.That((await fleet.CreateAsync("PAIR 700", null, paired.Email, null, null)).Succeeded, Is.True);
        Assert.That((await fleet.PairManuallyAsync((await LoadVehicleAsync("PAIR700")).Id, paired.Id)).Succeeded, Is.True);

        Assert.That((await fleet.CreateAsync("GONE 800", null, null, null, null)).Succeeded, Is.True);
        Assert.That((await fleet.SetActiveAsync((await LoadVehicleAsync("GONE800")).Id, false)).Succeeded, Is.True);

        var list = await fleet.ListAsync();
        CompanyVehicleDto Row(string plate) => list.Single(v => v.Plate == plate);

        Assert.That(Row("POOL 100").PairingState, Is.EqualTo(FleetPairingState.ManualOnly));
        Assert.That(Row("GHST 200").PairingState, Is.EqualTo(FleetPairingState.NoAccount));
        Assert.That(Row("PLAT 300").PairingState, Is.EqualTo(FleetPairingState.PlateMissing));
        Assert.That(Row("REDY 400").PairingState, Is.EqualTo(FleetPairingState.ReadyToPair));
        Assert.That(Row("CODE 500").PairingState, Is.EqualTo(FleetPairingState.CodeSent));
        Assert.That(Row("CODE 500").PairingCodeSentAtUtc, Is.Not.Null);
        Assert.That(Row("LOCK 600").PairingState, Is.EqualTo(FleetPairingState.Locked));
        Assert.That(Row("LOCK 600").PairingLockedUntilUtc, Is.Not.Null.And.GreaterThan(DateTimeOffset.UtcNow),
            "The admin sees when self-service reopens, not just that it is closed.");
        Assert.That(Row("PAIR 700").PairingState, Is.EqualTo(FleetPairingState.Paired));
        Assert.That(Row("PAIR 700").PairedAtUtc, Is.Not.Null);
        Assert.That(Row("GONE 800").PairingState, Is.EqualTo(FleetPairingState.Inactive));
    }

    [Test]
    public async Task Pairing_never_evicts_an_existing_resident_from_the_spot()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var fleet = CreateFleetService(scope);
        var spots = CreateSpotService();

        var claimant = await CreateUserAsync(userManager, "claimant", plate: "7MN 5555");
        var resident = await CreateUserAsync(userManager, "resident", plate: null);
        var spotId = await CreateSpotAsync("FL-04");
        Assert.That((await spots.AssignOwnerAsync(spotId, resident.Id)).Succeeded, Is.True);
        Assert.That((await fleet.CreateAsync("7MN 5555", null, claimant.Email, spotId, null)).Succeeded, Is.True);

        Assert.That((await fleet.RequestPairingCodeAsync(claimant.Id)).Succeeded, Is.True);
        var refused = await fleet.ConfirmPairingAsync(claimant.Id, await GenerateCodeAsync(userManager, claimant));
        Assert.That(refused.Errors, Does.Contain("Fleet_Error_SpotOccupied"),
            "An occupied spot is an administrative conflict; pairing must not resolve it by eviction.");

        Assert.That((await LoadVehicleAsync("7MN5555")).PairedUserId, Is.Null,
            "A pairing whose residency cannot materialize must not stand.");
        await using var dbContext = new D3ParkingDbContext(_options);
        var owner = await dbContext.ParkingSpots.Where(s => s.Id == spotId).Select(s => s.OwnerId).SingleAsync();
        Assert.That(owner, Is.EqualTo(resident.Id));
    }

    private FleetService CreateFleetService(AsyncServiceScope scope, INotificationService? notifications = null) => new(
        new TestDbContextFactory(_options),
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
        CreateSpotService(),
        new NullEmailSender(),
        notifications ?? new NullNotificationService(),
        new PassthroughLocalizer<ParkingMessages>(),
        TimeProvider.System,
        NullLogger<FleetService>.Instance);

    private ParkingSpotService CreateSpotService() => new(
        new TestDbContextFactory(_options),
        new NullNotificationService(),
        new FakeSiteSettings(),
        TimeProvider.System,
        new PassthroughLocalizer<ParkingMessages>());

    private static async Task<ApplicationUser> CreateUserAsync(UserManager<ApplicationUser> userManager, string prefix, string? plate)
    {
        var user = new ApplicationUser
        {
            UserName = $"{prefix}-{Guid.NewGuid():N}@test.local",
            Email = $"{prefix}-{Guid.NewGuid():N}@test.local",
            EmailConfirmed = true,
            Status = AccountStatus.Active,
            LicensePlate = plate,
        };
        var created = await userManager.CreateAsync(user, "Str0ng!Password");
        Assert.That(created.Succeeded, Is.True, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user;
    }

    private async Task<Guid> CreateSpotAsync(string code)
    {
        var result = await CreateSpotService().CreateAsync(code, ParkingSpotType.Standard, null);
        Assert.That(result.Succeeded, Is.True, string.Join("; ", result.Errors));
        await using var dbContext = new D3ParkingDbContext(_options);
        return await dbContext.ParkingSpots.Where(s => s.Code == code).Select(s => s.Id).SingleAsync();
    }

    private async Task<CompanyVehicle> LoadVehicleAsync(string normalizedPlate)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        return await dbContext.CompanyVehicles.AsNoTracking().SingleAsync(v => v.NormalizedPlate == normalizedPlate);
    }

    /// <summary>Reproduces the emailed code: same user, same provider, same purpose string.</summary>
    private async Task<string> GenerateCodeAsync(UserManager<ApplicationUser> userManager, ApplicationUser user)
    {
        var vehicle = await LoadVehicleAsync(PlateNormalizer.Normalize(user.LicensePlate!));
        var purpose = $"PairVehicle:{vehicle.Id}:{vehicle.DriverEmail?.ToUpperInvariant()}";
        return await userManager.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, purpose);
    }
}
