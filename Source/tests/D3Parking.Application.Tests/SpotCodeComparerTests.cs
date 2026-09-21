using D3Parking.Application.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class SpotCodeComparerTests
{
    [Test]
    public void Numeric_runs_compare_as_numbers_not_text()
    {
        var codes = new[] { "D3-10", "D3-2", "D3-1" };

        Array.Sort(codes, SpotCodeComparer.Instance);

        Assert.That(codes, Is.EqualTo(new[] { "D3-1", "D3-2", "D3-10" }));
    }

    [Test]
    public void Mixed_sections_sort_alphabetically_then_numerically()
    {
        var codes = new[] { "B-1", "A-10", "A-9", "A2", "A-1" };

        Array.Sort(codes, SpotCodeComparer.Instance);

        // "A2" has no separator: after the "A" run, '2' compares against '-' by character.
        Assert.That(codes, Is.EqualTo(new[] { "A-1", "A-9", "A-10", "A2", "B-1" }));
    }

    [Test]
    public void Comparison_ignores_case()
    {
        Assert.That(SpotCodeComparer.Instance.Compare("a-2", "A-10"), Is.LessThan(0));
        Assert.That(SpotCodeComparer.Instance.Compare("B-1", "b-1"), Is.EqualTo(0));
        Assert.That(SpotCodeComparer.Instance.Compare("a-1", "B-1"), Is.LessThan(0));
    }

    [Test]
    public void Equal_numeric_values_order_by_fewer_leading_zeros_first()
    {
        var codes = new[] { "A-01", "A-1", "A-001" };

        Array.Sort(codes, SpotCodeComparer.Instance);

        Assert.That(codes, Is.EqualTo(new[] { "A-1", "A-01", "A-001" }));
    }

    [Test]
    public void Shorter_prefix_of_a_longer_code_sorts_first()
    {
        Assert.That(SpotCodeComparer.Instance.Compare("A-1", "A-1b"), Is.LessThan(0));
        Assert.That(SpotCodeComparer.Instance.Compare("P2", "P2-08"), Is.LessThan(0));
    }

    [Test]
    public void Large_numbers_beyond_int_range_still_compare()
    {
        Assert.That(SpotCodeComparer.Instance.Compare("X99999999999999999998", "X99999999999999999999"), Is.LessThan(0));
    }
}
