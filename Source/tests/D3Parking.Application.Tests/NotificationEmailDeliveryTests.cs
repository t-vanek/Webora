using D3Parking.Domain.Notifications;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public sealed class NotificationEmailDeliveryTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void New_delivery_is_immediately_due()
    {
        var delivery = Create();

        Assert.Multiple(() =>
        {
            Assert.That(delivery.Status, Is.EqualTo(NotificationDeliveryStatus.Pending));
            Assert.That(delivery.Attempts, Is.Zero);
            Assert.That(delivery.CreatedAtUtc, Is.EqualTo(Start));
            Assert.That(delivery.NextAttemptUtc, Is.EqualTo(Start));
        });
    }

    [Test]
    public void Transient_failures_use_progressive_backoff()
    {
        var delivery = Create();
        var expectedDelays = new[] { 1, 5, 30, 120 };

        foreach (var delay in expectedDelays)
        {
            var failedAt = Start.AddHours(delivery.Attempts);
            delivery.MarkFailed(failedAt, "SMTP unavailable");

            Assert.Multiple(() =>
            {
                Assert.That(delivery.Status, Is.EqualTo(NotificationDeliveryStatus.Pending));
                Assert.That(delivery.NextAttemptUtc, Is.EqualTo(failedAt.AddMinutes(delay)));
            });
        }

        delivery.MarkFailed(Start.AddHours(4), "SMTP unavailable");

        Assert.Multiple(() =>
        {
            Assert.That(delivery.Status, Is.EqualTo(NotificationDeliveryStatus.Failed));
            Assert.That(delivery.Attempts, Is.EqualTo(5));
        });
    }

    [Test]
    public void Successful_delivery_closes_the_outbox_item()
    {
        var delivery = Create();
        var sentAt = Start.AddSeconds(10);

        delivery.MarkSent(sentAt);

        Assert.Multiple(() =>
        {
            Assert.That(delivery.Status, Is.EqualTo(NotificationDeliveryStatus.Sent));
            Assert.That(delivery.Attempts, Is.EqualTo(1));
            Assert.That(delivery.SentAtUtc, Is.EqualTo(sentAt));
            Assert.That(delivery.LastAttemptUtc, Is.EqualTo(sentAt));
        });
    }

    [Test]
    public void Manual_retry_reopens_only_a_permanently_failed_delivery()
    {
        var delivery = Create();
        Assert.That(delivery.QueueManualRetry(Start.AddHours(1)), Is.False);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            delivery.MarkFailed(Start.AddMinutes(attempt), "SMTP unavailable");
        }

        var queuedAt = Start.AddHours(2);
        var queued = delivery.QueueManualRetry(queuedAt);

        Assert.Multiple(() =>
        {
            Assert.That(queued, Is.True);
            Assert.That(delivery.Status, Is.EqualTo(NotificationDeliveryStatus.Pending));
            Assert.That(delivery.Attempts, Is.Zero);
            Assert.That(delivery.ManualRetryCount, Is.EqualTo(1));
            Assert.That(delivery.NextAttemptUtc, Is.EqualTo(queuedAt));
            Assert.That(delivery.LastError, Is.EqualTo("SMTP unavailable"));
        });
    }

    private static NotificationEmailDelivery Create() =>
        new(Guid.NewGuid(), "Title", "Message", "Open", "/parking", "Today", Start);
}
