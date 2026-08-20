using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using D3Parking.Application.Notifications;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Notifications;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Notifications;

public sealed class NotificationRuleService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IMemoryCache cache,
    TimeProvider timeProvider) : INotificationRuleService
{
    private const string CacheKey = "d3parking:notification-delivery-rules";

    public async Task<IReadOnlyList<NotificationDeliveryRuleDto>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out var cached) && cached is IReadOnlyList<NotificationDeliveryRuleDto> rules)
        {
            return rules;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await dbContext.NotificationDeliveryRules.AsNoTracking().ToListAsync(cancellationToken);
        rules = AllKeys().Select(key =>
        {
            var rule = stored.FirstOrDefault(r => r.Category == key.Category && r.Level == key.Level)
                ?? NotificationDeliveryRule.CreateDefault(key.Category, key.Level);
            return ToDto(rule);
        }).ToList();
        cache.Set(CacheKey, rules, TimeSpan.FromMinutes(5));
        return rules;
    }

    public async Task<NotificationDeliveryRuleDto> GetAsync(NotificationCategory category, NotificationLevel level,
        CancellationToken cancellationToken = default) =>
        (await GetAsync(cancellationToken)).Single(r => r.Category == category && r.Level == level);

    public async Task UpdateAsync(IReadOnlyCollection<NotificationDeliveryRuleDto> rules, Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var stored = await dbContext.NotificationDeliveryRules.ToListAsync(cancellationToken);
        foreach (var key in AllKeys())
        {
            var input = rules.FirstOrDefault(r => r.Category == key.Category && r.Level == key.Level)
                ?? throw new ArgumentException($"Missing notification rule {key.Category}/{key.Level}.", nameof(rules));
            var rule = stored.FirstOrDefault(r => r.Category == key.Category && r.Level == key.Level);
            if (rule is null)
            {
                dbContext.NotificationDeliveryRules.Add(new NotificationDeliveryRule(
                    key.Category, key.Level, input.InboxEnabled, input.LiveEnabled, input.EmailMode));
            }
            else
            {
                rule.Update(input.InboxEnabled, input.LiveEnabled, input.EmailMode);
            }
        }

        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            actingUserId, AccountAuditEventType.SettingsChanged, $"admin:{actingUserId}",
            "Notification delivery matrix changed.", timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    private static IEnumerable<(NotificationCategory Category, NotificationLevel Level)> AllKeys() =>
        from category in Enum.GetValues<NotificationCategory>()
        from level in Enum.GetValues<NotificationLevel>()
        select (category, level);

    private static NotificationDeliveryRuleDto ToDto(NotificationDeliveryRule rule) =>
        new(rule.Category, rule.Level, rule.InboxEnabled, rule.LiveEnabled, rule.EmailMode,
            rule.Level is NotificationLevel.Security or NotificationLevel.Critical);
}
