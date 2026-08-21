using D3Parking.Domain.Notifications;

namespace D3Parking.Application.Notifications;

public static class NotificationDeliveryRuleValidator
{
    public const string Error = "Parking_Settings_NotificationRulesInvalid";

    public static string? Validate(IReadOnlyCollection<NotificationDeliveryRuleDto> rules)
    {
        var expectedCount = Enum.GetValues<NotificationCategory>().Length
            * Enum.GetValues<NotificationLevel>().Length;
        if (rules.Count != expectedCount)
        {
            return Error;
        }

        var keys = new HashSet<(NotificationCategory Category, NotificationLevel Level)>();
        foreach (var rule in rules)
        {
            if (!Enum.IsDefined(rule.Category)
                || !Enum.IsDefined(rule.Level)
                || !Enum.IsDefined(rule.EmailMode)
                || !keys.Add((rule.Category, rule.Level)))
            {
                return Error;
            }

            var inboxMandatory = rule.Level is NotificationLevel.Security or NotificationLevel.Critical;
            if (rule.InboxMandatory != inboxMandatory
                || inboxMandatory && !rule.InboxEnabled
                || rule.LiveEnabled && !rule.InboxEnabled)
            {
                return Error;
            }
        }

        return null;
    }
}
