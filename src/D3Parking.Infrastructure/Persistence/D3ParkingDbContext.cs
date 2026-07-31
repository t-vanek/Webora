using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Oversight;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Domain.Settings;
using D3Parking.Infrastructure.Identity;

namespace D3Parking.Infrastructure.Persistence;

public class D3ParkingDbContext(DbContextOptions<D3ParkingDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    /// <summary>Backs <see cref="OversightCase.Number"/>; named here so the mapping and the migration agree.</summary>
    public const string OversightCaseNumberSequence = "OversightCaseNumbers";

    /// <summary>Shadow insertion ordinal on <see cref="OversightCaseEvent"/>: the timeline's tiebreaker.</summary>
    public const string OversightEventOrdinal = "Ordinal";

    public DbSet<PermissionGroup> PermissionGroups => Set<PermissionGroup>();

    public DbSet<RolePermissionGroup> RolePermissionGroups => Set<RolePermissionGroup>();

    public DbSet<ExternalRoleMapping> ExternalRoleMappings => Set<ExternalRoleMapping>();

    public DbSet<ExternalRoleAssignment> ExternalRoleAssignments => Set<ExternalRoleAssignment>();

    public DbSet<AccountAuditEvent> AccountAuditEvents => Set<AccountAuditEvent>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreferences> NotificationPreferences => Set<NotificationPreferences>();

    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();

    public DbSet<EntraSettings> EntraSettings => Set<EntraSettings>();

    public DbSet<ParkingSpot> ParkingSpots => Set<ParkingSpot>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<ParkerScore> ParkerScores => Set<ParkerScore>();

    public DbSet<PointsLedgerEntry> PointsLedgerEntries => Set<PointsLedgerEntry>();

    public DbSet<UserBadge> UserBadges => Set<UserBadge>();

    public DbSet<ParkingSettings> ParkingSettings => Set<ParkingSettings>();

    public DbSet<SpotRelease> SpotReleases => Set<SpotRelease>();

    public DbSet<QueueEntry> QueueEntries => Set<QueueEntry>();

    public DbSet<OccupancyMismatch> OccupancyMismatches => Set<OccupancyMismatch>();

    public DbSet<MismatchPhoto> MismatchPhotos => Set<MismatchPhoto>();

    public DbSet<ApologyVoucher> ApologyVouchers => Set<ApologyVoucher>();

    public DbSet<AvailabilityCampaign> AvailabilityCampaigns => Set<AvailabilityCampaign>();

    public DbSet<VisitorBooking> VisitorBookings => Set<VisitorBooking>();

    public DbSet<CollusionFlag> CollusionFlags => Set<CollusionFlag>();

    public DbSet<CompanyVehicle> CompanyVehicles => Set<CompanyVehicle>();

    public DbSet<OversightCase> OversightCases => Set<OversightCase>();

    public DbSet<OversightCaseEvent> OversightCaseEvents => Set<OversightCaseEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(AccountStatus.PendingActivation);

            user.Property(u => u.DisplayName).HasMaxLength(256);
            user.Property(u => u.Department).HasMaxLength(128);
            user.Property(u => u.StatusReason).HasMaxLength(512);
            user.Property(u => u.HomeAddress).HasMaxLength(512);

            user.Property(u => u.ExternalObjectId).HasMaxLength(64);
            user.Property(u => u.ExternalProvider).HasMaxLength(64);
            user.Property(u => u.ExternalTenantId).HasMaxLength(64);
            user.Ignore(u => u.IsFederated);

            // Filtered so the many purely local accounts (all null) do not collide with each other,
            // while a directory object can still only ever be one account here.
            user.HasIndex(u => new { u.ExternalProvider, u.ExternalObjectId })
                .IsUnique()
                .HasFilter("[ExternalObjectId] IS NOT NULL");
        });

        builder.Entity<PermissionGroup>(group =>
        {
            group.ToTable("PermissionGroups");
            group.HasKey(g => g.Id);
            group.Property(g => g.Name).HasMaxLength(128).IsRequired();
            group.Property(g => g.Description).HasMaxLength(512);
            group.HasIndex(g => g.Name).IsUnique();
            // The computed union of the entries; never a column of its own.
            group.Ignore(g => g.Permissions);

            group.HasMany(g => g.Entries)
                .WithOne()
                .HasForeignKey(e => e.PermissionGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PermissionGroupEntry>(entry =>
        {
            entry.ToTable("PermissionGroupEntries");
            entry.HasKey(e => new { e.PermissionGroupId, e.Permission });
            entry.Property(e => e.Permission).HasMaxLength(128).IsRequired();
        });

        builder.Entity<RolePermissionGroup>(link =>
        {
            link.ToTable("RolePermissionGroups");
            link.HasKey(l => new { l.RoleId, l.PermissionGroupId });
            link.HasIndex(l => l.PermissionGroupId);

            // Deleting either end drops the link, but never silently: the services refuse to delete
            // a role that still has members or a group that is still composed into a role.
            link.HasOne<ApplicationRole>()
                .WithMany()
                .HasForeignKey(l => l.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            link.HasOne<PermissionGroup>()
                .WithMany()
                .HasForeignKey(l => l.PermissionGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ExternalRoleMapping>(mapping =>
        {
            mapping.ToTable("ExternalRoleMappings");
            mapping.HasKey(m => m.Id);
            mapping.Property(m => m.Provider).HasMaxLength(64).IsRequired();
            mapping.Property(m => m.ExternalRole).HasMaxLength(256).IsRequired();
            // One directory role maps to one local role: two rows for the same claim would make
            // "what does this app role grant" a question with two answers.
            mapping.HasIndex(m => new { m.Provider, m.ExternalRole }).IsUnique();

            mapping.HasOne<ApplicationRole>()
                .WithMany()
                .HasForeignKey(m => m.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ExternalRoleAssignment>(assignment =>
        {
            assignment.ToTable("ExternalRoleAssignments");
            assignment.HasKey(a => new { a.UserId, a.RoleId });
            assignment.Property(a => a.Provider).HasMaxLength(64).IsRequired();

            assignment.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            assignment.HasOne<ApplicationRole>()
                .WithMany()
                .HasForeignKey(a => a.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AccountAuditEvent>(audit =>
        {
            audit.ToTable("AccountAuditEvents");
            audit.HasKey(e => e.Id);
            audit.Property(e => e.Type).HasConversion<string>().HasMaxLength(48);
            audit.Property(e => e.Actor).HasMaxLength(64).IsRequired();
            audit.Property(e => e.Detail).HasMaxLength(1024);
            audit.HasIndex(e => new { e.UserId, e.OccurredAtUtc });
        });

        builder.Entity<Notification>(notification =>
        {
            notification.ToTable("Notifications");
            notification.HasKey(n => n.Id);
            notification.Property(n => n.Category).HasConversion<string>().HasMaxLength(32);
            notification.Property(n => n.Level).HasConversion<string>().HasMaxLength(32);
            notification.Property(n => n.Title).HasMaxLength(256).IsRequired();
            notification.Property(n => n.Message).HasMaxLength(2048).IsRequired();
            notification.HasIndex(n => new { n.UserId, n.ReadAtUtc });
            notification.HasIndex(n => new { n.UserId, n.CreatedAtUtc });
        });

        builder.Entity<NotificationPreferences>(prefs =>
        {
            prefs.ToTable("NotificationPreferences");
            prefs.HasKey(p => p.UserId);
            prefs.Property(p => p.UserId).ValueGeneratedNever();
            prefs.Property(p => p.Scope).HasConversion<string>().HasMaxLength(32);
        });

        builder.Entity<PushSubscription>(subscription =>
        {
            subscription.ToTable("PushSubscriptions");
            subscription.HasKey(s => s.Id);
            // 800 chars keeps the unique index under SQL Server's 1700-byte nonclustered key cap;
            // real push endpoints (FCM, Mozilla, WNS) stay well below that.
            subscription.Property(s => s.Endpoint).HasMaxLength(800).IsRequired();
            subscription.Property(s => s.P256dh).HasMaxLength(128).IsRequired();
            subscription.Property(s => s.Auth).HasMaxLength(64).IsRequired();
            subscription.HasIndex(s => s.Endpoint).IsUnique();
            subscription.HasIndex(s => s.UserId);
        });

        builder.Entity<OccupancyMismatch>(mismatch =>
        {
            mismatch.ToTable("OccupancyMismatches");
            mismatch.HasKey(m => m.Id);
            mismatch.Property(m => m.BlockerPlate).HasMaxLength(16);
            // The admin view reads per-spot trends newest-first; the reporter index backs the
            // per-user daily report cap.
            mismatch.HasIndex(m => new { m.SpotId, m.ReportedAtUtc });
            mismatch.HasIndex(m => new { m.ReporterId, m.ReportedAtUtc });
        });

        builder.Entity<Identity.ApplicationUser>(user =>
        {
            user.Property(u => u.LicensePlate).HasMaxLength(16);
        });

        builder.Entity<AvailabilityCampaign>(campaign =>
        {
            campaign.ToTable("AvailabilityCampaigns");
            campaign.HasKey(c => c.Id);
            // Dedup lookups: newest campaign, and period-overlap checks.
            campaign.HasIndex(c => c.CreatedAtUtc);
            campaign.HasIndex(c => new { c.PeriodStart, c.PeriodEnd });
        });

        builder.Entity<VisitorBooking>(booking =>
        {
            booking.ToTable("VisitorBookings");
            booking.HasKey(b => b.Id);
            booking.Property(b => b.VisitorName).HasMaxLength(128).IsRequired();
            booking.Property(b => b.Company).HasMaxLength(128);
            booking.Property(b => b.LicensePlate).HasMaxLength(16);
            booking.Property(b => b.Status).HasConversion<string>().HasMaxLength(16);
            // Conflict checks per spot and window; the agenda lists by start.
            booking.HasIndex(b => new { b.SpotId, b.StartUtc });
            booking.HasIndex(b => b.StartUtc);
        });

        builder.Entity<ApologyVoucher>(voucher =>
        {
            voucher.ToTable("ApologyVouchers");
            voucher.HasKey(v => v.Id);
            voucher.Property(v => v.Status).HasConversion<string>().HasMaxLength(16);
            // Lookup shape: "the user's usable voucher" and "the voucher paid for reservation X".
            voucher.HasIndex(v => new { v.UserId, v.RedeemedAtUtc, v.ExpiresAtUtc });
            voucher.HasIndex(v => v.RedeemedReservationId);
            // The admin review view resolves "the voucher this mismatch granted".
            voucher.HasIndex(v => v.SourceMismatchId);
            // Shadow rowversion: the review (approve/reject) races the holder's redeem/restore
            // and a second reviewer — a stale ruling must fail and re-read, not win silently.
            voucher.Property<byte[]>("Version").IsRowVersion();
        });

        builder.Entity<MismatchPhoto>(photo =>
        {
            photo.ToTable("MismatchPhotos");
            photo.HasKey(p => p.Id);
            photo.Property(p => p.ContentType).HasMaxLength(64).IsRequired();
            photo.Property(p => p.ContentHash).HasMaxLength(32).IsRequired();
            // One photo per report, and one report per photo — the unique fingerprint index is
            // the hard backstop that the same picture can never prove two mismatches.
            photo.HasIndex(p => p.MismatchId).IsUnique();
            photo.HasIndex(p => p.ContentHash).IsUnique();
        });

        builder.Entity<SiteSettings>(settings =>
        {
            settings.ToTable("SiteSettings");
            settings.HasKey(s => s.Id);
            settings.Property(s => s.Id).ValueGeneratedNever();
            settings.Property(s => s.CanonicalHost).HasMaxLength(253);
            settings.Property(s => s.DefaultLanguage).HasMaxLength(16);
            settings.Property(s => s.DefaultTimeZoneId).HasMaxLength(64);
            settings.Property(s => s.PageCharset).HasMaxLength(32);
            settings.Property(s => s.EmailCharset).HasMaxLength(32);
            settings.Property(s => s.DefaultRole).HasMaxLength(256);
            settings.Property(s => s.Scheme).HasConversion<string>().HasMaxLength(8);
            settings.Property(s => s.WwwPreference).HasConversion<string>().HasMaxLength(16);
            settings.Property(s => s.TrailingSlash).HasConversion<string>().HasMaxLength(16);
            settings.Property(s => s.SiteName).HasMaxLength(128);
            settings.Property(s => s.SiteDescription).HasMaxLength(512);
            settings.Property(s => s.Aliases)
                .HasColumnType("nvarchar(max)")
                .HasConversion(
                    list => string.Join('\n', list),
                    value => value.Length == 0
                        ? Array.Empty<string>()
                        : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    new ValueComparer<IReadOnlyList<string>>(
                        (a, b) => a!.SequenceEqual(b!),
                        v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                        v => v.ToArray()));
        });

        builder.Entity<EntraSettings>(entra =>
        {
            entra.ToTable("EntraSettings");
            entra.HasKey(s => s.Id);
            entra.Property(s => s.Id).ValueGeneratedNever();
            entra.Property(s => s.TenantId).HasMaxLength(64);
            entra.Property(s => s.ClientId).HasMaxLength(64);
            entra.Property(s => s.Authority).HasMaxLength(512);
            entra.Property(s => s.CallbackPath).HasMaxLength(256).IsRequired();
            entra.Property(s => s.SignedOutCallbackPath).HasMaxLength(256).IsRequired();
            entra.Property(s => s.DisplayName).HasMaxLength(64).IsRequired();
            // Ciphertext, not the secret: data protection payloads are base64 and grow with the key
            // ring's metadata, so they get room rather than a length that would truncate one.
            entra.Property(s => s.ClientSecretProtected).HasColumnType("nvarchar(max)");
            entra.Property(s => s.ScimBearerTokenProtected).HasColumnType("nvarchar(max)");
            entra.Property(s => s.DefaultRoles)
                .HasColumnType("nvarchar(max)")
                .HasConversion(
                    list => string.Join('\n', list),
                    value => value.Length == 0
                        ? Array.Empty<string>()
                        : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    new ValueComparer<IReadOnlyList<string>>(
                        (a, b) => a!.SequenceEqual(b!),
                        v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                        v => v.ToArray()));
        });

        builder.Entity<ParkingSpot>(spot =>
        {
            spot.ToTable("ParkingSpots");
            spot.HasKey(s => s.Id);
            spot.Property(s => s.Code).HasMaxLength(32).IsRequired();
            spot.Property(s => s.Type).HasConversion<string>().HasMaxLength(32);
            spot.Property(s => s.Notes).HasMaxLength(512);
            spot.Property(s => s.PlannedUseDays).HasConversion<int>();
            spot.HasIndex(s => s.Code).IsUnique();
            // One spot per resident: every resident flow resolves "the user's spot" with a single
            // FirstOrDefault, so duplicate ownership would make those flows nondeterministic.
            // AssignOwnerAsync checks first; the filtered unique index is the backstop.
            spot.HasIndex(s => s.OwnerId).IsUnique().HasFilter("[OwnerId] IS NOT NULL");
        });

        builder.Entity<SpotRelease>(release =>
        {
            release.ToTable("SpotReleases");
            release.HasKey(r => r.Id);
            release.HasIndex(r => new { r.SpotId, r.Date }).IsUnique();
            release.HasIndex(r => new { r.OwnerId, r.Date });
        });

        builder.Entity<QueueEntry>(entry =>
        {
            entry.ToTable("QueueEntries");
            entry.HasKey(q => q.Id);
            entry.Property(q => q.Status).HasConversion<string>().HasMaxLength(32);
            entry.HasIndex(q => new { q.Status, q.CreatedAtUtc });
            entry.HasIndex(q => new { q.UserId, q.Status });
            // Shadow rowversion: a queue entry is mutated both by the user (leave, claim) and by
            // the maintenance loop (offer, expire) — a stale write must fail, not win.
            entry.Property<byte[]>("Version").IsRowVersion();
        });

        builder.Entity<CompanyVehicle>(vehicle =>
        {
            vehicle.ToTable("CompanyVehicles");
            vehicle.HasKey(v => v.Id);
            vehicle.Property(v => v.Plate).HasMaxLength(16).IsRequired();
            vehicle.Property(v => v.NormalizedPlate).HasMaxLength(16).IsRequired();
            // Pre-existing registry rows are the company cars the registry was built for.
            vehicle.Property(v => v.Type)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasDefaultValue(VehicleType.Company);
            vehicle.Property(v => v.Name).HasMaxLength(128);
            vehicle.Property(v => v.DriverEmail).HasMaxLength(256);
            vehicle.Property(v => v.Notes).HasMaxLength(512);
            // One vehicle per plate — the plate is the registry's natural key.
            vehicle.HasIndex(v => v.NormalizedPlate).IsUnique();
            // A spot hosts at most one vehicle and a user pairs with at most one vehicle;
            // both mirror the one-spot-per-resident invariant the pairing materializes into.
            vehicle.HasIndex(v => v.AssignedSpotId).IsUnique().HasFilter("[AssignedSpotId] IS NOT NULL");
            vehicle.HasIndex(v => v.PairedUserId).IsUnique().HasFilter("[PairedUserId] IS NOT NULL");
            // Shadow rowversion: two users confirming a claim on the same vehicle (or an admin
            // editing while a user pairs) must not overwrite each other silently.
            vehicle.Property<byte[]>("Version").IsRowVersion();
        });

        builder.Entity<CollusionFlag>(flag =>
        {
            flag.ToTable("CollusionFlags");
            flag.HasKey(f => f.Id);
            flag.HasIndex(f => new { f.UserA, f.UserB }).IsUnique();
        });

        builder.Entity<OversightCase>(oversight =>
        {
            oversight.ToTable("OversightCases");
            oversight.HasKey(c => c.Id);
            oversight.Property(c => c.Kind).HasConversion<string>().HasMaxLength(32);
            oversight.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            oversight.Property(c => c.Priority).HasConversion<string>().HasMaxLength(16);
            oversight.Property(c => c.Resolution).HasConversion<string>().HasMaxLength(24);
            oversight.Property(c => c.ResolutionNote).HasMaxLength(2048);

            // The short handle reviewers quote to each other, from a sequence rather than a count:
            // two cases opened in the same sweep must not race to the same number, and a number
            // must never be reused after a case is gone.
            oversight.Property(c => c.Number)
                .HasDefaultValueSql($"NEXT VALUE FOR [{OversightCaseNumberSequence}]")
                .ValueGeneratedOnAdd();
            oversight.HasIndex(c => c.Number).IsUnique();

            // One case per signal — the ingest is idempotent because of this index, not because it
            // remembers what it has already seen.
            oversight.HasIndex(c => new { c.Kind, c.SubjectId }).IsUnique();
            // The queue's shapes: the open list newest-activity-first, and "mine".
            oversight.HasIndex(c => new { c.Status, c.UpdatedAtUtc });
            oversight.HasIndex(c => new { c.AssigneeId, c.Status });
            // Two sweeps read by this one: the overdue view, and the breach detection that has to
            // find the handful of open cases past their deadline without walking the archive.
            oversight.HasIndex(c => new { c.Status, c.DueAtUtc });
            // Recurrence counts reports per spot within a window.
            oversight.HasIndex(c => new { c.SpotId, c.OpenedAtUtc });

            // Shadow rowversion: two reviewers land on the same fresh case and both press a
            // button. The loser must re-read and meet the guard ("already decided"), not overwrite
            // the ruling that got there first.
            oversight.Property<byte[]>("Version").IsRowVersion();
        });

        builder.Entity<OversightCaseEvent>(entry =>
        {
            entry.ToTable("OversightCaseEvents");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
            entry.Property(e => e.Actor).HasConversion<string>().HasMaxLength(16);
            entry.Property(e => e.Visibility).HasConversion<string>().HasMaxLength(16);
            entry.Property(e => e.Body).HasMaxLength(4000);
            entry.HasIndex(e => new { e.CaseId, e.OccurredAtUtc });

            // Two entries can share a timestamp — ruling on a voucher writes its own line and the
            // case's close-out in one act — and then a timestamp alone cannot order them. The
            // insertion ordinal is what keeps the history in the order it happened; it is a shadow
            // property because insertion order is the storage's business, not the domain's.
            entry.Property<long>(OversightEventOrdinal).ValueGeneratedOnAdd().UseIdentityColumn();

            // The history dies with the case it belongs to and with nothing else.
            entry.HasOne<OversightCase>()
                .WithMany()
                .HasForeignKey(e => e.CaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.HasSequence<int>(OversightCaseNumberSequence).StartsAt(1).IncrementsBy(1);

        builder.Entity<Reservation>(reservation =>
        {
            reservation.ToTable("Reservations");
            reservation.HasKey(r => r.Id);
            reservation.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            reservation.HasIndex(r => new { r.SpotId, r.StartUtc });
            reservation.HasIndex(r => new { r.UserId, r.StartUtc });
            reservation.HasIndex(r => r.Status);
            // Shadow rowversion: status transitions race between the user (check-in, cancel,
            // release) and the no-show sweep. The in-memory transition guard only validates each
            // context's stale copy, so the last blind UPDATE used to win — with the token a stale
            // write throws DbUpdateConcurrencyException and the caller re-reads instead.
            reservation.Property<byte[]>("Version").IsRowVersion();
        });

        builder.Entity<ParkerScore>(score =>
        {
            score.ToTable("ParkerScores");
            score.HasKey(s => s.UserId);
            score.Property(s => s.UserId).ValueGeneratedNever();
            score.HasIndex(s => s.Points);
            // Shadow rowversion: the wallet is read-modify-write from reservations, refunds,
            // sweeps and monthly grants; concurrent writers must not overwrite each other's
            // balance (the ledger would diverge from the wallet).
            score.Property<byte[]>("Version").IsRowVersion();
        });

        builder.Entity<PointsLedgerEntry>(entry =>
        {
            entry.ToTable("PointsLedgerEntries");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Reason).HasConversion<string>().HasMaxLength(32);
            entry.Property(e => e.Detail).HasMaxLength(512);
            entry.HasIndex(e => new { e.UserId, e.OccurredAtUtc });
        });

        builder.Entity<UserBadge>(badge =>
        {
            badge.ToTable("UserBadges");
            badge.HasKey(b => b.Id);
            badge.Property(b => b.Badge).HasConversion<string>().HasMaxLength(32);
            badge.HasIndex(b => new { b.UserId, b.Badge }).IsUnique();
        });

        builder.Entity<ParkingSettings>(settings =>
        {
            settings.ToTable("ParkingSettings");
            settings.HasKey(s => s.Id);
            settings.Property(s => s.Id).ValueGeneratedNever();
            // Economy defaults so existing settings rows get sensible pricing when the columns are added.
            settings.Property(s => s.BaseReservationCost).HasDefaultValue(10);
            settings.Property(s => s.PeakPricePercent).HasDefaultValue(200);
            settings.Property(s => s.OccupancyPricePercent).HasDefaultValue(100);
            settings.Property(s => s.MaxReservationCost).HasDefaultValue(40);
            settings.Property(s => s.MonthlyCreditAllowance).HasDefaultValue(100);
            settings.Property(s => s.QueueOfferMinutes).HasDefaultValue(15);
            settings.Property(s => s.QueueNoShowPenaltyPoints).HasDefaultValue(50);
            settings.Property(s => s.QueueNoShowCreditPenalty).HasDefaultValue(30);
            settings.Property(s => s.QueueNoShowBanDays).HasDefaultValue(14);
            settings.Property(s => s.QueueNoShowAllowancePenalty).HasDefaultValue(30);
            settings.Property(s => s.DemandReleaseOccupancyPercent).HasDefaultValue(100);
            settings.Property(s => s.DemandReleaseQueueBonus).HasDefaultValue(5);
            settings.Property(s => s.MaxReleaseReward).HasDefaultValue(40);
            settings.Property(s => s.StreakBonusPerLevel).HasDefaultValue(2);
            settings.Property(s => s.StreakBonusCap).HasDefaultValue(20);
            settings.Property(s => s.TierSilverPoints).HasDefaultValue(50);
            settings.Property(s => s.TierGoldPoints).HasDefaultValue(150);
            settings.Property(s => s.TierPlatinumPoints).HasDefaultValue(300);
            settings.Property(s => s.QueuePriorityPerTier).HasDefaultValue(30);
            settings.Property(s => s.TierAllowanceBonus).HasDefaultValue(20);
            settings.Property(s => s.TierDiscountPercent).HasDefaultValue(5);
            settings.Property(s => s.ReputationDecayPercent).HasDefaultValue(10);
            settings.Property(s => s.ReputationDecayIntervalDays).HasDefaultValue(30);
            settings.Property(s => s.AdaptivePricingEnabled).HasDefaultValue(false);
            settings.Property(s => s.AdaptiveTargetOccupancyPercent).HasDefaultValue(85);
            settings.Property(s => s.AdaptiveGainPercent).HasDefaultValue(100);
            settings.Property(s => s.AdaptiveDeadbandPercent).HasDefaultValue(5);
            settings.Property(s => s.AdaptiveStepMaxPercent).HasDefaultValue(25);
            settings.Property(s => s.AdaptivePeakMinPercent).HasDefaultValue(100);
            settings.Property(s => s.AdaptivePeakMaxPercent).HasDefaultValue(400);
            settings.Property(s => s.AdaptiveIntervalMinutes).HasDefaultValue(60);
            settings.Property(s => s.TrustEnabled).HasDefaultValue(true);
            settings.Property(s => s.TrustIntervalHours).HasDefaultValue(24);
            settings.Property(s => s.TrustedBadgeThreshold).HasDefaultValue(60);
            settings.Property(s => s.MaxPairTrustWeight).HasDefaultValue(3);
            settings.Property(s => s.AntiCollusionEnabled).HasDefaultValue(true);
            settings.Property(s => s.CollusionMinInteractions).HasDefaultValue(4);
            settings.Property(s => s.CollusionConcentrationPercent).HasDefaultValue(70);
            settings.Property(s => s.CollusionScanIntervalHours).HasDefaultValue(24);
            settings.Property(s => s.OversightSlaCriticalHours).HasDefaultValue(4);
            settings.Property(s => s.OversightSlaHighHours).HasDefaultValue(24);
            settings.Property(s => s.OversightSlaNormalHours).HasDefaultValue(72);
            settings.Property(s => s.OversightSlaLowHours).HasDefaultValue(168);
            settings.Property(s => s.OversightRecurrenceWindowDays).HasDefaultValue(30);
            settings.Property(s => s.OversightRecurrenceThreshold).HasDefaultValue(3);
            settings.Property(s => s.OversightDigestHourLocal).HasDefaultValue(8);
        });

        // Registers the OpenIddict entity sets (applications, authorizations, scopes, tokens).
        builder.UseOpenIddict();
    }
}
