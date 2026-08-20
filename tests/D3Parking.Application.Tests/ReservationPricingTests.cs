using D3Parking.Domain.Parking.Incentives;
using D3Parking.Domain.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class ReservationPricingTests
{
    [Test]
    public void Planner_price_is_fixed_and_ignores_occupancy_and_legacy_peak()
    {
        var policy = new IncentivePolicy
        {
            BaseReservationCost = 10,
            PeakPricePercent = 400, // legacy setting must no longer affect a booking
            OccupancyPricePercent = 100,
            MaxReservationCost = 100,
        };

        Assert.That(policy.ComputeReservationCost(0), Is.EqualTo(10));
        Assert.That(policy.ComputeReservationCost(0.5), Is.EqualTo(10));
        Assert.That(policy.ComputeReservationCost(1), Is.EqualTo(10));
    }

    [Test]
    public void Planning_budget_tops_up_to_the_limit_instead_of_accumulating()
    {
        var score = new ParkerScore(Guid.NewGuid());
        var january = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var february = january.AddMonths(1);

        Assert.That(score.GrantCreditIfDue(100, ParkerScore.PeriodOf(january), january), Is.EqualTo(100));
        score.ChargeCredits(30, january.AddDays(1));
        Assert.That(score.GrantCreditIfDue(100, ParkerScore.PeriodOf(february), february), Is.EqualTo(30));
        Assert.That(score.Credits, Is.EqualTo(100));
    }

    [TestCase(BudgetRenewalPeriod.Daily, 20260820)]
    [TestCase(BudgetRenewalPeriod.Weekly, 20260817)]
    [TestCase(BudgetRenewalPeriod.Monthly, 20260801)]
    [TestCase(BudgetRenewalPeriod.Yearly, 20260101)]
    public void Budget_period_uses_the_local_calendar(BudgetRenewalPeriod period, int expected)
    {
        var prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var instant = new DateTimeOffset(2026, 8, 19, 22, 30, 0, TimeSpan.Zero);

        Assert.That(ParkerScore.PeriodOf(instant, period, prague), Is.EqualTo(expected));
    }
}
