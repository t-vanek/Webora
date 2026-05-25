using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Webora.Domain.Accounts;
using Webora.Domain.Notifications;
using Webora.Infrastructure.Identity;

namespace Webora.Infrastructure.Persistence;

public class WeboraDbContext(DbContextOptions<WeboraDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<AccountAuditEvent> AccountAuditEvents => Set<AccountAuditEvent>();

    public DbSet<Notification> Notifications => Set<Notification>();

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
            notification.Property(n => n.Level).HasConversion<string>().HasMaxLength(32);
            notification.Property(n => n.Title).HasMaxLength(256).IsRequired();
            notification.Property(n => n.Message).HasMaxLength(2048).IsRequired();
            notification.HasIndex(n => new { n.UserId, n.ReadAtUtc });
            notification.HasIndex(n => new { n.UserId, n.CreatedAtUtc });
        });

        // Registers the OpenIddict entity sets (applications, authorizations, scopes, tokens).
        builder.UseOpenIddict();
    }
}
