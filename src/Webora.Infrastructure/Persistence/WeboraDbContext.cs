using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Webora.Domain.Accounts;
using Webora.Domain.Notifications;
using Webora.Domain.Parking;
using Webora.Domain.Parking.Incentives;
using Webora.Domain.Settings;
using Webora.Infrastructure.Identity;

namespace Webora.Infrastructure.Persistence;

public class WeboraDbContext(DbContextOptions<WeboraDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<AccountAuditEvent> AccountAuditEvents => Set<AccountAuditEvent>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreferences> NotificationPreferences => Set<NotificationPreferences>();

    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();

    public DbSet<ParkingSpot> ParkingSpots => Set<ParkingSpot>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<ParkerScore> ParkerScores => Set<ParkerScore>();

    public DbSet<PointsLedgerEntry> PointsLedgerEntries => Set<PointsLedgerEntry>();

    public DbSet<UserBadge> UserBadges => Set<UserBadge>();

    public DbSet<ParkingSettings> ParkingSettings => Set<ParkingSettings>();

    public DbSet<SpotRelease> SpotReleases => Set<SpotRelease>();

    public DbSet<QueueEntry> QueueEntries => Set<QueueEntry>();

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
            user.Property(u => u.StatusReason).HasMaxLength(512);
            user.Property(u => u.HomeAddress).HasMaxLength(512);
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
                .HasColumnType("text")
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
            spot.HasIndex(s => s.Code).IsUnique();
            spot.HasIndex(s => s.OwnerId);
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
        });

        builder.Entity<Reservation>(reservation =>
        {
            reservation.ToTable("Reservations");
            reservation.HasKey(r => r.Id);
            reservation.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            reservation.HasIndex(r => new { r.SpotId, r.StartUtc });
            reservation.HasIndex(r => new { r.UserId, r.StartUtc });
            reservation.HasIndex(r => r.Status);
        });

        builder.Entity<ParkerScore>(score =>
        {
            score.ToTable("ParkerScores");
            score.HasKey(s => s.UserId);
            score.Property(s => s.UserId).ValueGeneratedNever();
            score.HasIndex(s => s.Points);
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
        });

        // Registers the OpenIddict entity sets (applications, authorizations, scopes, tokens).
        builder.UseOpenIddict();
    }
}
