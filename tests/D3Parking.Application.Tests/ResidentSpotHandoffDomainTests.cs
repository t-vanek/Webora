using D3Parking.Domain.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class ResidentSpotHandoffDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 8, 0, 0, TimeSpan.Zero);

    [Test]
    public void Resident_offer_waits_for_its_named_recipient()
    {
        var residentId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var handoff = ResidentSpotHandoff.CreateOffer(
            Guid.NewGuid(), residentId, recipientId,
            Now.AddDays(1), Now.AddDays(1).AddHours(8), Now, Now.AddHours(12));

        Assert.Multiple(() =>
        {
            Assert.That(handoff.Kind, Is.EqualTo(ResidentSpotHandoffKind.ResidentOffer));
            Assert.That(handoff.Status, Is.EqualTo(ResidentSpotHandoffStatus.Offered));
            Assert.That(handoff.IsActive, Is.True);
            Assert.That(handoff.ResidentId, Is.EqualTo(residentId));
            Assert.That(handoff.RecipientId, Is.EqualTo(recipientId));
        });
    }

    [Test]
    public void User_request_remembers_the_maximum_authorized_price()
    {
        var handoff = ResidentSpotHandoff.CreateRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Now.AddDays(1), Now.AddDays(1).AddHours(8), Now, Now.AddHours(12), 7);

        Assert.Multiple(() =>
        {
            Assert.That(handoff.Kind, Is.EqualTo(ResidentSpotHandoffKind.UserRequest));
            Assert.That(handoff.Status, Is.EqualTo(ResidentSpotHandoffStatus.PendingResident));
            Assert.That(handoff.MaxCreditsAuthorized, Is.EqualTo(7));
        });
    }

    [Test]
    public void Accepting_is_terminal_and_links_the_created_reservation()
    {
        var handoff = ResidentSpotHandoff.CreateOffer(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Now.AddDays(1), Now.AddDays(1).AddHours(8), Now, Now.AddHours(12));
        var reservationId = Guid.NewGuid();

        handoff.Accept(reservationId, Now.AddMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(handoff.Status, Is.EqualTo(ResidentSpotHandoffStatus.Accepted));
            Assert.That(handoff.ReservationId, Is.EqualTo(reservationId));
            Assert.That(handoff.IsActive, Is.False);
            Assert.Throws<InvalidOperationException>(() => handoff.Cancel(Now.AddMinutes(2)));
        });
    }

    [Test]
    public void Expired_handoff_cannot_be_accepted()
    {
        var handoff = ResidentSpotHandoff.CreateRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Now.AddDays(1), Now.AddDays(1).AddHours(8), Now, Now.AddHours(12), 0);

        handoff.Expire(Now.AddHours(12));

        Assert.That(handoff.Status, Is.EqualTo(ResidentSpotHandoffStatus.Expired));
        Assert.Throws<InvalidOperationException>(() => handoff.Accept(Guid.NewGuid(), Now.AddHours(13)));
    }

    [Test]
    public void Handoff_to_self_is_rejected()
    {
        var userId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => ResidentSpotHandoff.CreateOffer(
            Guid.NewGuid(), userId, userId,
            Now.AddDays(1), Now.AddDays(1).AddHours(8), Now, Now.AddHours(12)));
    }
}
