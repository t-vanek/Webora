using D3Parking.Domain.Notifications;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class NotificationDeliveryRuleTests
{
    [Test]
    public void Live_delivery_cannot_exist_without_an_inbox_record()
    {
        var rule = new NotificationDeliveryRule(NotificationCategory.SelfService, NotificationLevel.Info,
            inboxEnabled: false, liveEnabled: true, NotificationEmailMode.Never);

        Assert.Multiple(() =>
        {
            Assert.That(rule.InboxEnabled, Is.False);
            Assert.That(rule.LiveEnabled, Is.False);
        });
    }

    [TestCase(NotificationLevel.Security)]
    [TestCase(NotificationLevel.Critical)]
    public void Mandatory_events_always_keep_an_auditable_inbox_record(NotificationLevel level)
    {
        var rule = new NotificationDeliveryRule(NotificationCategory.Administrative, level,
            inboxEnabled: false, liveEnabled: false, NotificationEmailMode.Never);

        Assert.That(rule.InboxEnabled, Is.True);
    }

    [Test]
    public void Critical_events_default_to_all_delivery_channels()
    {
        var rule = NotificationDeliveryRule.CreateDefault(
            NotificationCategory.Administrative, NotificationLevel.Critical);

        Assert.Multiple(() =>
        {
            Assert.That(rule.InboxEnabled, Is.True);
            Assert.That(rule.LiveEnabled, Is.True);
            Assert.That(rule.EmailMode, Is.EqualTo(NotificationEmailMode.Always));
        });
    }
}
