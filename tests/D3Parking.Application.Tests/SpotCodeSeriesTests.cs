using D3Parking.Application.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class SpotCodeSeriesTests
{
    [Test]
    public void Expands_a_single_section_with_separator_and_padding()
    {
        var result = SpotCodeSeries.Expand("A", "-", 1, 3, 2);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Codes, Is.EqualTo(new[] { "A-01", "A-02", "A-03" }));
    }

    [Test]
    public void Expands_every_section_across_the_whole_range_in_order()
    {
        var result = SpotCodeSeries.Expand("A, B", "", 1, 2, 0);

        Assert.That(result.Codes, Is.EqualTo(new[] { "A1", "A2", "B1", "B2" }));
    }

    [Test]
    public void Blank_sections_produce_bare_numbers_without_a_separator()
    {
        var result = SpotCodeSeries.Expand("  ", "-", 8, 10, 0);

        Assert.That(result.Codes, Is.EqualTo(new[] { "8", "9", "10" }));
    }

    [Test]
    public void Sections_are_trimmed_and_deduplicated_case_insensitively()
    {
        var result = SpotCodeSeries.Expand(" A ,a, B,, ", "-", 1, 1, 0);

        Assert.That(result.Codes, Is.EqualTo(new[] { "A-1", "B-1" }));
    }

    [Test]
    public void Padding_never_truncates_numbers_longer_than_the_width()
    {
        var result = SpotCodeSeries.Expand("A", "-", 99, 101, 2);

        Assert.That(result.Codes, Is.EqualTo(new[] { "A-99", "A-100", "A-101" }));
    }

    [Test]
    public void Reversed_range_fails_with_the_range_error()
    {
        var result = SpotCodeSeries.Expand("A", "-", 5, 1, 0);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.ErrorKey, Is.EqualTo("Parking_Error_SeriesRange"));
        Assert.That(result.Codes, Is.Empty);
    }

    [Test]
    public void Negative_from_and_oversized_pad_width_fail_with_the_range_error()
    {
        Assert.That(SpotCodeSeries.Expand("A", "-", -1, 3, 0).ErrorKey, Is.EqualTo("Parking_Error_SeriesRange"));
        Assert.That(SpotCodeSeries.Expand("A", "-", 1, 3, SpotCodeSeries.MaxPadWidth + 1).ErrorKey, Is.EqualTo("Parking_Error_SeriesRange"));
    }

    [Test]
    public void Batch_at_the_size_cap_succeeds_and_one_over_fails()
    {
        var atCap = SpotCodeSeries.Expand("A", "-", 1, SpotCodeSeries.MaxBatchSize, 0);
        var overCap = SpotCodeSeries.Expand("A", "-", 1, SpotCodeSeries.MaxBatchSize + 1, 0);

        Assert.That(atCap.Succeeded, Is.True);
        Assert.That(atCap.Codes, Has.Count.EqualTo(SpotCodeSeries.MaxBatchSize));
        Assert.That(overCap.ErrorKey, Is.EqualTo("Parking_Error_SeriesTooLarge"));
    }

    [Test]
    public void Cap_applies_to_the_cross_product_of_sections_and_range()
    {
        // 3 sections × 200 numbers = 600 > 500.
        var result = SpotCodeSeries.Expand("A, B, C", "-", 1, 200, 0);

        Assert.That(result.ErrorKey, Is.EqualTo("Parking_Error_SeriesTooLarge"));
    }
}
