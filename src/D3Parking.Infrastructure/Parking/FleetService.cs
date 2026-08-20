using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Email;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

/// <summary>
/// The company-vehicle registry and the plate→account pairing on top of it. A plate is public
/// information (readable off the car in the lot), so it is never sufficient on its own: pairing
/// requires the registry's driver email to match the account's confirmed email AND a code sent
/// to that address to be typed back. Residency itself is materialized through
/// <see cref="IParkingSpotService.AssignOwnerAsync"/>, so every resident invariant (one spot per
/// owner, no visitor spots, release revocation, notifications) applies unchanged.
/// </summary>
public sealed class FleetService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    UserManager<ApplicationUser> userManager,
    IParkingSpotService parkingSpots,
    IEmailSender emailSender,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages,
    TimeProvider timeProvider,
    ILogger<FleetService> logger) : IFleetService
{
    // Guardrails around the 6-digit Rfc6238 code: the code itself is stateless and short-lived,
    // but the attempt budget and the resend throttle must be persisted or the space could be
    // brute-forced and the mailbox spammed.
    private const int MaxCodeAttempts = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<CompanyVehicleDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await (from v in dbContext.CompanyVehicles.AsNoTracking()
                          join s in dbContext.ParkingSpots on v.AssignedSpotId equals s.Id into spots
                          from s in spots.DefaultIfEmpty()
                          join u in dbContext.Users on v.PairedUserId equals u.Id into users
                          from u in users.DefaultIfEmpty()
                          // The driver's prospective account, looked up the way Identity itself
                          // resolves emails. It only feeds the funnel column below — the
                          // authorization still happens in LoadPairableAsync at pairing time.
                          join d in dbContext.Users on v.DriverEmail!.ToUpper() equals d.NormalizedEmail into drivers
                          from d in drivers.DefaultIfEmpty()
                          select new
                          {
                              v.Id,
                              v.Plate,
                              v.NormalizedPlate,
                              v.Type,
                              v.Name,
                              v.DriverEmail,
                              v.AssignedSpotId,
                              SpotCode = s != null ? s.Code : null,
                              v.PairedUserId,
                              PairedUserName = u != null ? (u.DisplayName ?? u.Email) : null,
                              v.PairedAtUtc,
                              v.IsActive,
                              v.Notes,
                              v.PairingCodeSentAtUtc,
                              v.PairingAttempts,
                              v.PairingAttemptsWindowStartUtc,
                              DriverStatus = d != null ? (AccountStatus?)d.Status : null,
                              DriverEmailConfirmed = d != null && d.EmailConfirmed,
                              DriverPlate = d != null ? d.LicensePlate : null,
                          })
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        return rows
            .Select(r =>
            {
                var lockedUntil = LockedUntil(r.PairingAttempts, r.PairingAttemptsWindowStartUtc, now);

                // The funnel reads top-down: the first missing ingredient names the state.
                var state = !r.IsActive ? FleetPairingState.Inactive
                    : r.PairedUserId is not null ? FleetPairingState.Paired
                    : r.DriverEmail is null ? FleetPairingState.ManualOnly
                    : r.DriverStatus is not AccountStatus.Active || !r.DriverEmailConfirmed ? FleetPairingState.NoAccount
                    : r.DriverPlate is null || !string.Equals(PlateNormalizer.Normalize(r.DriverPlate), r.NormalizedPlate, StringComparison.Ordinal) ? FleetPairingState.PlateMissing
                    : lockedUntil is not null ? FleetPairingState.Locked
                    : r.PairingCodeSentAtUtc is not null ? FleetPairingState.CodeSent
                    : FleetPairingState.ReadyToPair;

                return new CompanyVehicleDto(
                    r.Id, r.Plate, r.Type, r.Name, r.DriverEmail, r.AssignedSpotId, r.SpotCode,
                    r.PairedUserId, r.PairedUserName, r.PairedAtUtc, r.IsActive, r.Notes,
                    state, r.PairingCodeSentAtUtc, lockedUntil);
            })
            .OrderBy(v => v.Plate, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PagedResult<CompanyVehicleDto>> ListAdminPageAsync(
        FleetVehicleListQuery filter,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize, 1, 100);
        var index = Math.Max(0, pageIndex);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = AdminRows(dbContext, timeProvider.GetUtcNow());

        var term = filter.Search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            query = query.Where(v =>
                v.Plate.Contains(term) ||
                (v.Name != null && v.Name.Contains(term)) ||
                (v.DriverEmail != null && v.DriverEmail.Contains(term)) ||
                (v.PairedUserName != null && v.PairedUserName.Contains(term)) ||
                (v.SpotCode != null && v.SpotCode.Contains(term)));
        }

        if (filter.Type is { } type)
        {
            query = query.Where(v => v.Type == type);
        }

        query = filter.Parking switch
        {
            FleetParkingAssignmentFilter.Assigned => query.Where(v => v.AssignedSpotId != null),
            FleetParkingAssignmentFilter.Unassigned => query.Where(v => v.AssignedSpotId == null),
            _ => query,
        };

        query = filter.State switch
        {
            FleetVehicleStateFilter.Paired => query.Where(v => v.IsActive && v.PairingState == FleetPairingState.Paired),
            FleetVehicleStateFilter.ActionRequired => query.Where(v => v.IsActive &&
                (v.PairingState == FleetPairingState.ManualOnly ||
                 v.PairingState == FleetPairingState.NoAccount ||
                 v.PairingState == FleetPairingState.PlateMissing ||
                 v.PairingState == FleetPairingState.Locked)),
            FleetVehicleStateFilter.ManualOnly => query.Where(v => v.IsActive && v.PairingState == FleetPairingState.ManualOnly),
            FleetVehicleStateFilter.Inactive => query.Where(v => !v.IsActive),
            _ => query.Where(v => v.IsActive),
        };

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return PagedResult<CompanyVehicleDto>.Empty(size);
        }

        index = Math.Min(index, (total - 1) / size);
        var rows = await query
            .OrderBy(v => v.PairingState == FleetPairingState.Locked ? 0
                : v.PairingState == FleetPairingState.NoAccount ? 1
                : v.PairingState == FleetPairingState.PlateMissing ? 2
                : v.PairingState == FleetPairingState.ManualOnly ? 3
                : v.PairingState == FleetPairingState.CodeSent ? 4
                : v.PairingState == FleetPairingState.ReadyToPair ? 5
                : v.PairingState == FleetPairingState.Paired ? 6
                : 7)
            .ThenBy(v => v.Plate)
            .Skip(index * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var items = rows.Select(row => new CompanyVehicleDto(
            row.Id, row.Plate, row.Type, row.Name, row.DriverEmail, row.AssignedSpotId, row.SpotCode,
            row.PairedUserId, row.PairedUserName, row.PairedAtUtc, row.IsActive, row.Notes,
            row.PairingState, row.PairingCodeSentAtUtc,
            LockedUntil(row.PairingAttempts, row.PairingAttemptsWindowStartUtc, now))).ToList();

        return new PagedResult<CompanyVehicleDto>(items, total, index, size);
    }

    public async Task<FleetDirectorySummary> GetAdminSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = AdminRows(dbContext, timeProvider.GetUtcNow());

        var groups = await rows
            .GroupBy(v => new { v.IsActive, v.PairingState, Assigned = v.AssignedSpotId != null })
            .Select(group => new
            {
                group.Key.IsActive,
                group.Key.PairingState,
                group.Key.Assigned,
                Count = group.Count(),
            })
            .ToListAsync(cancellationToken);

        var assignedSpotIds = await dbContext.CompanyVehicles.AsNoTracking()
            .Where(v => v.AssignedSpotId != null)
            .Select(v => v.AssignedSpotId!.Value)
            .ToListAsync(cancellationToken);

        var active = groups.Where(g => g.IsActive).Sum(g => g.Count);
        var inactive = groups.Where(g => !g.IsActive).Sum(g => g.Count);
        var paired = groups.Where(g => g.IsActive && g.PairingState == FleetPairingState.Paired).Sum(g => g.Count);
        var manual = groups.Where(g => g.IsActive && g.PairingState == FleetPairingState.ManualOnly).Sum(g => g.Count);
        var action = groups.Where(g => g.IsActive && g.PairingState is
            FleetPairingState.ManualOnly or FleetPairingState.NoAccount or FleetPairingState.PlateMissing or FleetPairingState.Locked)
            .Sum(g => g.Count);
        var assigned = groups.Where(g => g.IsActive && g.Assigned).Sum(g => g.Count);

        return new FleetDirectorySummary(active + inactive, active, paired, action, manual, inactive, assigned, assignedSpotIds);
    }

    public Task<ParkingResult> CreateAsync(string plate, VehicleType type, string? name, string? driverEmail, Guid? spotId, string? notes, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(plate) || PlateNormalizer.Normalize(plate).Length == 0)
            {
                return ParkingResult.Failure("Fleet_Error_PlateRequired");
            }

            // The entitlement rule: a dedicated spot goes with a company vehicle only.
            if (type == VehicleType.Employee && spotId is not null)
            {
                return ParkingResult.Failure("Fleet_Error_EmployeeVehicleNoSpot");
            }

            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            if (spotId is { } spot && await ValidateSpotAsync(dbContext, spot, vehicleId: null, cancellationToken) is { } invalidSpot)
            {
                return invalidSpot;
            }

            var vehicle = new CompanyVehicle(plate, type, Trimmed(name), NormalizedEmail(driverEmail), spotId, Trimmed(notes), timeProvider.GetUtcNow());
            dbContext.CompanyVehicles.Add(vehicle);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
            {
                // Duplicate plate or a concurrently taken spot; the unique indexes are the last
                // line of defence behind the checks above.
                return ParkingResult.Failure("Fleet_Error_DuplicatePlate");
            }

            await NudgeDriverIfPairableAsync(vehicle, cancellationToken);
            return ParkingResult.Success;
        }, cancellationToken);

    public Task<ParkingResult> UpdateAsync(Guid id, string plate, VehicleType type, string? name, string? driverEmail, Guid? spotId, string? notes, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(plate) || PlateNormalizer.Normalize(plate).Length == 0)
            {
                return ParkingResult.Failure("Fleet_Error_PlateRequired");
            }

            // Same entitlement rule as CreateAsync; demoting a company car therefore forces the
            // admin to clear its spot in the same edit, and the residency moves out with it below.
            if (type == VehicleType.Employee && spotId is not null)
            {
                return ParkingResult.Failure("Fleet_Error_EmployeeVehicleNoSpot");
            }

            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var vehicle = await dbContext.CompanyVehicles.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
            if (vehicle is null)
            {
                return ParkingResult.Failure("Fleet_Error_VehicleNotFound");
            }

            var newEmail = NormalizedEmail(driverEmail);
            var newNormalizedPlate = PlateNormalizer.Normalize(plate);
            var previousSpotId = vehicle.AssignedSpotId;

            if (spotId is { } spot && spot != previousSpotId
                && await ValidateSpotAsync(dbContext, spot, vehicle.Id, cancellationToken) is { } invalidSpot)
            {
                return invalidSpot;
            }

            var identityEdited = !string.Equals(vehicle.NormalizedPlate, newNormalizedPlate, StringComparison.Ordinal)
                || !string.Equals(vehicle.DriverEmail, newEmail, StringComparison.OrdinalIgnoreCase);

            // A pairing is a validated (plate, email, code) triple; when the admin edits either
            // identity component, the validation no longer speaks for the paired user, so the
            // pairing dissolves (and its residency with it). A spot change alone keeps the
            // pairing — the resident just moves with the vehicle.
            if (identityEdited && vehicle.PairedUserId is not null)
            {
                var unpaired = await ReleasePairingAsync(dbContext, vehicle, notifyUser: true, cancellationToken);
                if (!unpaired.Succeeded)
                {
                    return unpaired;
                }
            }

            vehicle.Rename(plate);
            vehicle.ChangeType(type);
            vehicle.Update(Trimmed(name), newEmail, Trimmed(notes));
            vehicle.AssignSpot(spotId);

            if (vehicle.PairedUserId is { } pairedUser && spotId != previousSpotId)
            {
                var moved = await MoveResidencyAsync(pairedUser, previousSpotId, spotId, cancellationToken);
                if (!moved.Succeeded)
                {
                    return moved;
                }
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
            {
                return ParkingResult.Failure("Fleet_Error_DuplicatePlate");
            }

            // The new identity may match someone who could not pair before (a fixed typo in the
            // email, a corrected plate) — close the loop the same way account activation does.
            if (identityEdited && vehicle.PairedUserId is null)
            {
                await NudgeDriverIfPairableAsync(vehicle, cancellationToken);
            }

            return ParkingResult.Success;
        }, cancellationToken);

    public Task<ParkingResult> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var vehicle = await dbContext.CompanyVehicles.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
            if (vehicle is null)
            {
                return ParkingResult.Failure("Fleet_Error_VehicleNotFound");
            }

            var wasActive = vehicle.IsActive;
            if (active)
            {
                vehicle.Activate();
            }
            else
            {
                // A returned/decommissioned car takes its pairing (and the residency) with it.
                var released = await ReleasePairingAsync(dbContext, vehicle, notifyUser: true, cancellationToken);
                if (!released.Succeeded)
                {
                    return released;
                }

                vehicle.Deactivate();
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            // A reactivated car is pairable again — nudge the driver like a fresh registration.
            if (active && !wasActive)
            {
                await NudgeDriverIfPairableAsync(vehicle, cancellationToken);
            }

            return ParkingResult.Success;
        }, cancellationToken);

    public Task<ParkingResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var vehicle = await dbContext.CompanyVehicles.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
            if (vehicle is null)
            {
                return ParkingResult.Failure("Fleet_Error_VehicleNotFound");
            }

            var released = await ReleasePairingAsync(dbContext, vehicle, notifyUser: true, cancellationToken);
            if (!released.Succeeded)
            {
                return released;
            }

            dbContext.CompanyVehicles.Remove(vehicle);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ParkingResult.Success;
        }, cancellationToken);

    public Task<ParkingResult> PairManuallyAsync(Guid id, Guid userId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var vehicle = await dbContext.CompanyVehicles.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
            if (vehicle is null)
            {
                return ParkingResult.Failure("Fleet_Error_VehicleNotFound");
            }

            if (!vehicle.IsActive)
            {
                return ParkingResult.Failure("Fleet_Error_VehicleInactive");
            }

            if (vehicle.PairedUserId is not null)
            {
                return ParkingResult.Failure("Fleet_Error_AlreadyPaired");
            }

            if (!await dbContext.Users.AnyAsync(u => u.Id == userId, cancellationToken))
            {
                return ParkingResult.Failure("Parking_Error_UserNotFound");
            }

            if (await dbContext.CompanyVehicles.AnyAsync(v => v.PairedUserId == userId, cancellationToken))
            {
                return ParkingResult.Failure("Fleet_Error_UserAlreadyPaired");
            }

            return await CompletePairingAsync(dbContext, vehicle, userId, cancellationToken);
        }, cancellationToken);

    public Task<ParkingResult> UnpairAsync(Guid id, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var vehicle = await dbContext.CompanyVehicles.FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
            if (vehicle is null)
            {
                return ParkingResult.Failure("Fleet_Error_VehicleNotFound");
            }

            var released = await ReleasePairingAsync(dbContext, vehicle, notifyUser: true, cancellationToken);
            if (!released.Succeeded)
            {
                return released;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return ParkingResult.Success;
        }, cancellationToken);

    public async Task<PairingStatusDto> GetMyPairingStatusAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // A pairing is the user's own state, not something to re-derive from the profile plate:
        // a manually paired pool-car driver (no plate in the profile at all) still sees their
        // pairing — and someone paired elsewhere is never offered a second code ceremony.
        var paired = await dbContext.CompanyVehicles.AsNoTracking()
            .FirstOrDefaultAsync(v => v.PairedUserId == userId, cancellationToken);
        if (paired is not null)
        {
            return new PairingStatusDto(VehiclePairingState.Paired, paired.Plate, paired.Name, paired.Type,
                await SpotCodeAsync(dbContext, paired.AssignedSpotId, cancellationToken), null);
        }

        var user = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.LicensePlate, u.Email })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.LicensePlate))
        {
            return new PairingStatusDto(VehiclePairingState.NoPlate, null, null, null, null, null);
        }

        var normalized = PlateNormalizer.Normalize(user.LicensePlate);
        var vehicle = await dbContext.CompanyVehicles.AsNoTracking()
            .FirstOrDefaultAsync(v => v.NormalizedPlate == normalized && v.IsActive, cancellationToken);
        if (vehicle is null)
        {
            return new PairingStatusDto(VehiclePairingState.NoMatch, user.LicensePlate, null, null, null, null);
        }

        // Deliberately one opaque state for "paired to someone else", "no driver email on file"
        // and "email mismatch": the difference would tell a prober what the registry knows.
        if (vehicle.PairedUserId is not null
            || vehicle.DriverEmail is null
            || !string.Equals(vehicle.DriverEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            return new PairingStatusDto(VehiclePairingState.NotPairable, user.LicensePlate, null, null, null, null);
        }

        return new PairingStatusDto(VehiclePairingState.CodeRequired, vehicle.Plate, vehicle.Name, vehicle.Type,
            await SpotCodeAsync(dbContext, vehicle.AssignedSpotId, cancellationToken), vehicle.PairingCodeSentAtUtc);
    }

    public Task<ParkingResult> RequestPairingCodeAsync(Guid userId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var (failure, user, vehicle) = await LoadPairableAsync(dbContext, userId, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var now = timeProvider.GetUtcNow();
            if (vehicle!.PairingCodeSentAtUtc is { } sentAt && now - sentAt < ResendInterval)
            {
                return ParkingResult.Failure("Fleet_Error_CodeThrottled");
            }

            var code = await userManager.GenerateUserTokenAsync(user!, TokenOptions.DefaultEmailProvider, PairingPurpose(vehicle));
            await SendCodeEmailAsync(user!, vehicle, code, cancellationToken);

            vehicle.RegisterCodeSent(now);
            await dbContext.SaveChangesAsync(cancellationToken);
            return ParkingResult.Success;
        }, cancellationToken);

    public Task<ParkingResult> ConfirmPairingAsync(Guid userId, string code, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(async () =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var (failure, user, vehicle) = await LoadPairableAsync(dbContext, userId, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var now = timeProvider.GetUtcNow();
            if (LockedUntil(vehicle!.PairingAttempts, vehicle.PairingAttemptsWindowStartUtc, now) is not null)
            {
                return ParkingResult.Failure("Fleet_Error_TooManyAttempts");
            }

            var valid = !string.IsNullOrWhiteSpace(code)
                && await userManager.VerifyUserTokenAsync(user!, TokenOptions.DefaultEmailProvider, PairingPurpose(vehicle), code.Trim());
            if (!valid)
            {
                vehicle.RegisterFailedAttempt(now, AttemptWindow);
                await dbContext.SaveChangesAsync(cancellationToken);
                return ParkingResult.Failure("Fleet_Error_CodeInvalid");
            }

            return await CompletePairingAsync(dbContext, vehicle, userId, cancellationToken);
        }, cancellationToken);

    public Task SyncUserPlateAsync(Guid userId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync<object?>(async () =>
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var vehicle = await dbContext.CompanyVehicles.FirstOrDefaultAsync(v => v.PairedUserId == userId, cancellationToken);
            if (vehicle is null)
            {
                return null;
            }

            var plate = await dbContext.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.LicensePlate)
                .FirstOrDefaultAsync(cancellationToken);
            var stillMatches = !string.IsNullOrWhiteSpace(plate)
                && string.Equals(PlateNormalizer.Normalize(plate), vehicle.NormalizedPlate, StringComparison.Ordinal);
            if (stillMatches)
            {
                return null;
            }

            // The residency exists because of the company car; removing the plate from the
            // profile severs that claim, so the spot goes back with the vehicle.
            var released = await ReleasePairingAsync(dbContext, vehicle, notifyUser: true, cancellationToken);
            if (released.Succeeded)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                logger.LogWarning("Unpairing vehicle {Plate} after a plate change failed: {Error}",
                    vehicle.Plate, released.Errors.FirstOrDefault());
            }

            return null;
        }, cancellationToken);

    public async Task NotifyPairableAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var status = await GetMyPairingStatusAsync(userId, cancellationToken);
        if (status.State != VehiclePairingState.CodeRequired)
        {
            return;
        }

        await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
            messages["Fleet_Notify_Pairable_Title"],
            messages["Fleet_Notify_Pairable_Body", status.VehiclePlate ?? string.Empty],
            email: true, cancellationToken);
    }

    /// <summary>
    /// Pairs the vehicle and materializes residency. The pairing save runs FIRST: the vehicle
    /// row's rowversion plus the unique PairedUserId index let exactly one claimant through, and
    /// only that winner touches the spot afterwards — a loser must have no side effects to undo
    /// (its concurrency exception propagates to the retry wrapper before any spot mutation).
    /// </summary>
    private async Task<ParkingResult> CompletePairingAsync(D3ParkingDbContext dbContext, CompanyVehicle vehicle, Guid userId, CancellationToken cancellationToken)
    {
        Guid? pendingSpot = null;
        if (vehicle.AssignedSpotId is { } spotId)
        {
            var owner = await dbContext.ParkingSpots.AsNoTracking()
                .Where(s => s.Id == spotId)
                .Select(s => new { s.OwnerId })
                .FirstOrDefaultAsync(cancellationToken);
            if (owner is null)
            {
                return ParkingResult.Failure("Parking_Error_SpotNotFound");
            }

            // A different manual resident on the vehicle's spot is an administrative conflict the
            // pairing must not resolve by evicting anyone.
            if (owner.OwnerId is { } existing && existing != userId)
            {
                return ParkingResult.Failure("Fleet_Error_SpotOccupied");
            }

            if (owner.OwnerId != userId)
            {
                pendingSpot = spotId;
            }
        }

        vehicle.Pair(userId, timeProvider.GetUtcNow());
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (OptimisticConcurrency.IsUniqueViolation(ex))
        {
            // Someone else claimed this vehicle (or this user another vehicle) between our read
            // and our save.
            return ParkingResult.Failure("Fleet_Error_AlreadyPaired");
        }

        if (pendingSpot is { } assign)
        {
            var assigned = await parkingSpots.AssignOwnerAsync(assign, userId, cancellationToken);
            if (!assigned.Succeeded)
            {
                // The residency is the point of the pairing; without it the pairing must not
                // stand. We hold the row's latest version, so this save only races an admin edit
                // landing in this very instant — then the pairing stays and an unpair fixes it.
                try
                {
                    vehicle.Unpair();
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex)
                {
                    logger.LogWarning(ex, "Compensating unpair of vehicle {Plate} after a failed spot assignment did not land", vehicle.Plate);
                }

                return assigned;
            }
        }

        await notifications.NotifyAsync(userId, NotificationCategory.SelfService, NotificationLevel.Info,
            messages["Fleet_Notify_Paired_Title"],
            messages["Fleet_Notify_Paired_Body", vehicle.Plate], cancellationToken);
        return ParkingResult.Success;
    }

    /// <summary>Dissolves a pairing: releases the residency it carried, unpairs, notifies.</summary>
    private async Task<ParkingResult> ReleasePairingAsync(D3ParkingDbContext dbContext, CompanyVehicle vehicle, bool notifyUser, CancellationToken cancellationToken)
    {
        if (vehicle.PairedUserId is not { } pairedUser)
        {
            return ParkingResult.Success;
        }

        if (vehicle.AssignedSpotId is { } spotId)
        {
            var ownedByPairedUser = await dbContext.ParkingSpots.AsNoTracking()
                .AnyAsync(s => s.Id == spotId && s.OwnerId == pairedUser, cancellationToken);
            if (ownedByPairedUser)
            {
                var released = await parkingSpots.AssignOwnerAsync(spotId, null, cancellationToken);
                if (!released.Succeeded)
                {
                    return released;
                }
            }
        }

        vehicle.Unpair();

        if (notifyUser)
        {
            await notifications.NotifyAsync(pairedUser, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Fleet_Notify_Unpaired_Title"],
                messages["Fleet_Notify_Unpaired_Body", vehicle.Plate], cancellationToken);
        }

        return ParkingResult.Success;
    }

    /// <summary>The self-service preconditions: active account, confirmed email, full three-way match.</summary>
    private async Task<(ParkingResult? Failure, ApplicationUser? User, CompanyVehicle? Vehicle)> LoadPairableAsync(
        D3ParkingDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.Status != AccountStatus.Active || !user.EmailConfirmed)
        {
            return (ParkingResult.Failure("Fleet_Error_NotPairable"), null, null);
        }

        if (string.IsNullOrWhiteSpace(user.LicensePlate))
        {
            return (ParkingResult.Failure("Fleet_Error_PlateMissing"), null, null);
        }

        var normalized = PlateNormalizer.Normalize(user.LicensePlate);
        var vehicle = await dbContext.CompanyVehicles
            .FirstOrDefaultAsync(v => v.NormalizedPlate == normalized && v.IsActive, cancellationToken);

        // One opaque failure for every "close but not yours" case — see GetMyPairingStatusAsync.
        if (vehicle is null
            || vehicle.PairedUserId is not null
            || vehicle.DriverEmail is null
            || !string.Equals(vehicle.DriverEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            return (ParkingResult.Failure("Fleet_Error_NotPairable"), null, null);
        }

        if (await dbContext.CompanyVehicles.AnyAsync(v => v.PairedUserId == userId, cancellationToken))
        {
            return (ParkingResult.Failure("Fleet_Error_UserAlreadyPaired"), null, null);
        }

        return (null, user, vehicle);
    }

    private async Task<ParkingResult> MoveResidencyAsync(Guid pairedUser, Guid? previousSpotId, Guid? newSpotId, CancellationToken cancellationToken)
    {
        if (previousSpotId is { } previous)
        {
            var released = await parkingSpots.AssignOwnerAsync(previous, null, cancellationToken);
            if (!released.Succeeded)
            {
                return released;
            }
        }

        if (newSpotId is { } next)
        {
            var assigned = await parkingSpots.AssignOwnerAsync(next, pairedUser, cancellationToken);
            if (!assigned.Succeeded)
            {
                return assigned;
            }
        }

        return ParkingResult.Success;
    }

    private async Task<ParkingResult?> ValidateSpotAsync(D3ParkingDbContext dbContext, Guid spotId, Guid? vehicleId, CancellationToken cancellationToken)
    {
        var spot = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.Id == spotId)
            .Select(s => new { s.Type })
            .FirstOrDefaultAsync(cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        // Visitor spots belong to the reception's agenda; a fleet car cannot be a resident there
        // (the same rule AssignOwnerAsync enforces at materialization time).
        if (spot.Type == ParkingSpotType.Visitor)
        {
            return ParkingResult.Failure("Fleet_Error_VisitorSpot");
        }

        if (await dbContext.CompanyVehicles.AnyAsync(
                v => v.AssignedSpotId == spotId && v.Id != vehicleId, cancellationToken))
        {
            return ParkingResult.Failure("Fleet_Error_SpotTaken");
        }

        return null;
    }

    /// <summary>
    /// SQL-translatable projection shared by the paged registry and its global summary. Keeping the
    /// funnel state inside the query is what lets filtering happen before Skip/Take instead of
    /// materialising the entire fleet in the Blazor circuit.
    /// </summary>
    private static IQueryable<FleetAdminRow> AdminRows(D3ParkingDbContext dbContext, DateTimeOffset now)
    {
        var openAttemptWindow = now - AttemptWindow;

        return from v in dbContext.CompanyVehicles.AsNoTracking()
               join s in dbContext.ParkingSpots on v.AssignedSpotId equals s.Id into spots
               from s in spots.DefaultIfEmpty()
               join u in dbContext.Users on v.PairedUserId equals u.Id into users
               from u in users.DefaultIfEmpty()
               join d in dbContext.Users on v.DriverEmail!.ToUpper() equals d.NormalizedEmail into drivers
               from d in drivers.DefaultIfEmpty()
               select new FleetAdminRow
               {
                   Id = v.Id,
                   Plate = v.Plate,
                   Type = v.Type,
                   Name = v.Name,
                   DriverEmail = v.DriverEmail,
                   AssignedSpotId = v.AssignedSpotId,
                   SpotCode = s != null ? s.Code : null,
                   PairedUserId = v.PairedUserId,
                   PairedUserName = u != null ? (u.DisplayName ?? u.Email) : null,
                   PairedAtUtc = v.PairedAtUtc,
                   IsActive = v.IsActive,
                   Notes = v.Notes,
                   PairingCodeSentAtUtc = v.PairingCodeSentAtUtc,
                   PairingAttempts = v.PairingAttempts,
                   PairingAttemptsWindowStartUtc = v.PairingAttemptsWindowStartUtc,
                   PairingState = !v.IsActive ? FleetPairingState.Inactive
                       : v.PairedUserId != null ? FleetPairingState.Paired
                       : v.DriverEmail == null ? FleetPairingState.ManualOnly
                       : d == null || d.Status != AccountStatus.Active || !d.EmailConfirmed ? FleetPairingState.NoAccount
                       : d.LicensePlate == null ||
                         d.LicensePlate.Replace(" ", "").Replace("-", "").ToUpper() != v.NormalizedPlate
                           ? FleetPairingState.PlateMissing
                       : v.PairingAttempts >= MaxCodeAttempts &&
                         v.PairingAttemptsWindowStartUtc != null &&
                         v.PairingAttemptsWindowStartUtc >= openAttemptWindow
                           ? FleetPairingState.Locked
                       : v.PairingCodeSentAtUtc != null ? FleetPairingState.CodeSent
                       : FleetPairingState.ReadyToPair,
               };
    }

    /// <summary>When the failed-attempt budget is spent, the moment self-service reopens; else null.</summary>
    private static DateTimeOffset? LockedUntil(int attempts, DateTimeOffset? windowStartUtc, DateTimeOffset now) =>
        attempts >= MaxCodeAttempts && windowStartUtc is { } start && now - start <= AttemptWindow
            ? start + AttemptWindow
            : null;

    private static async Task<string?> SpotCodeAsync(D3ParkingDbContext dbContext, Guid? spotId, CancellationToken cancellationToken) =>
        spotId is { } id
            ? await dbContext.ParkingSpots.AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => s.Code)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

    /// <summary>
    /// Closes the loop after a registry change: when the vehicle's driver email belongs to an
    /// active account, that user gets the same "you can pair" nudge account activation sends —
    /// without it, a car registered after the account already exists sits unnoticed until the
    /// driver happens to open their profile. Advisory: the registry change has already
    /// committed, so a notification hiccup must not read back to the admin as a failed save.
    /// </summary>
    private async Task NudgeDriverIfPairableAsync(CompanyVehicle vehicle, CancellationToken cancellationToken)
    {
        if (vehicle.DriverEmail is null)
        {
            return;
        }

        try
        {
            var user = await userManager.FindByEmailAsync(vehicle.DriverEmail);
            if (user is { Status: AccountStatus.Active, EmailConfirmed: true })
            {
                await NotifyPairableAsync(user.Id, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fleet pairing nudge for vehicle {Plate} failed", vehicle.Plate);
        }
    }

    // The purpose binds the code to this vehicle AND the driver email it was issued for: an admin
    // correcting either between request and confirm silently invalidates outstanding codes.
    private static string PairingPurpose(CompanyVehicle vehicle) =>
        $"PairVehicle:{vehicle.Id}:{vehicle.DriverEmail?.ToUpperInvariant()}";

    private async Task SendCodeEmailAsync(ApplicationUser user, CompanyVehicle vehicle, string code, CancellationToken cancellationToken)
    {
        // The driver email equals the account email by precondition; sending to the registry's
        // copy keeps the addressing anchored to what the fleet manager approved.
        var html = BrandedEmail.RenderHtml(new BrandedEmail.Content(
            Heading: messages["Fleet_Email_Code_Subject"].Value,
            BodyHtml: messages["Fleet_Email_Code_Body", vehicle.Plate].Value
                + $"""<p style="font-size:1.5rem;letter-spacing:.35em;margin:16px 0;"><strong>{System.Net.WebUtility.HtmlEncode(code)}</strong></p>""",
            FooterReason: messages["Email_Chrome_Reason"].Value,
            FooterSettings: messages["Email_Chrome_Settings"].Value,
            DeadlineText: messages["Fleet_Email_Code_Deadline"].Value));

        await emailSender.SendAsync(new EmailMessage
        {
            To = vehicle.DriverEmail!,
            ToName = user.DisplayName,
            Subject = messages["Fleet_Email_Code_Subject"].Value,
            HtmlBody = html,
        }, cancellationToken);
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizedEmail(string? email) => string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    private sealed class FleetAdminRow
    {
        public Guid Id { get; init; }
        public required string Plate { get; init; }
        public VehicleType Type { get; init; }
        public string? Name { get; init; }
        public string? DriverEmail { get; init; }
        public Guid? AssignedSpotId { get; init; }
        public string? SpotCode { get; init; }
        public Guid? PairedUserId { get; init; }
        public string? PairedUserName { get; init; }
        public DateTimeOffset? PairedAtUtc { get; init; }
        public bool IsActive { get; init; }
        public string? Notes { get; init; }
        public FleetPairingState PairingState { get; init; }
        public DateTimeOffset? PairingCodeSentAtUtc { get; init; }
        public int PairingAttempts { get; init; }
        public DateTimeOffset? PairingAttemptsWindowStartUtc { get; init; }
    }
}
