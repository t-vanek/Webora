using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public sealed class ParkingNotificationTextTests
{
    private readonly PassthroughLocalizer<ParkingMessages> _messages = new();

    [Test]
    public void EconomyMode_IsDerivedFromBaseReservationCost()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new IncentivePolicy { BaseReservationCost = 10 }.CreditsEnabled, Is.True);
            Assert.That(new IncentivePolicy { BaseReservationCost = 0 }.CreditsEnabled, Is.False);
        });
    }

    [TestCase(10, "Parking_Notify_Reserved_Body")]
    [TestCase(0, "Parking_Notify_Reserved_Body_NoCredits")]
    public void CopySelector_UsesTheVariantForTheConfiguredEconomy(int baseCost, string expectedKey)
    {
        var policy = new IncentivePolicy { BaseReservationCost = baseCost };

        var selected = _messages.ForEconomy(policy, "Parking_Notify_Reserved_Body", "D3-2", 10);

        Assert.That(selected.Name, Is.EqualTo(expectedKey));
    }
}
