using D3Parking.Application.Parking;
using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public sealed class CalendarIcsRendererTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly DateTimeOffset Created = new(2026, 8, 20, 7, 0, 0, TimeSpan.Zero);

    [Test]
    public void Reservation_changes_increment_calendar_revision()
    {
        var reservation = new Reservation(
            Guid.NewGuid(), Guid.NewGuid(), Created.AddDays(1), Created.AddDays(1).AddHours(8), false, Created);

        reservation.MoveTo(Guid.NewGuid(), Created.AddMinutes(1));
        reservation.Cancel(Created.AddMinutes(2));

        Assert.Multiple(() =>
        {
            Assert.That(reservation.CalendarSequence, Is.EqualTo(2));
            Assert.That(reservation.CalendarUpdatedAtUtc, Is.EqualTo(Created.AddMinutes(2)));
        });
    }

    [Test]
    public void Feed_keeps_stable_uid_and_marks_cancelled_revision()
    {
        var id = Guid.NewGuid();
        var dto = CreateDto(id, ReservationStatus.Cancelled, sequence: 3);

        var ics = CalendarIcsRenderer.BuildFeed([dto], "D3 Parking", Prague);

        Assert.Multiple(() =>
        {
            Assert.That(ics, Does.Contain($"UID:{id:D}@d3parking\r\n"));
            Assert.That(ics, Does.Contain("SEQUENCE:3\r\n"));
            Assert.That(ics, Does.Contain("STATUS:CANCELLED\r\n"));
            Assert.That(ics, Does.Not.Contain("BEGIN:VALARM"));
            Assert.That(ics, Does.EndWith("END:VCALENDAR\r\n"));
        });
    }

    [Test]
    public void Local_calendar_day_is_emitted_as_an_exclusive_date_range()
    {
        var day = new DateOnly(2026, 8, 21);
        var window = SiteTime.Day(day, Prague);
        var dto = CreateDto(Guid.NewGuid(), ReservationStatus.Reserved, start: window.Start, end: window.End);

        var ics = CalendarIcsRenderer.BuildSingle(dto, "D3 Parking", Prague);

        Assert.Multiple(() =>
        {
            Assert.That(ics, Does.Contain("DTSTART;VALUE=DATE:20260821\r\n"));
            Assert.That(ics, Does.Contain("DTEND;VALUE=DATE:20260822\r\n"));
            Assert.That(ics, Does.Not.Contain("BEGIN:VALARM"));
        });
    }

    [Test]
    public void Content_lines_are_folded_without_exceeding_seventy_five_utf8_octets()
    {
        var dto = CreateDto(Guid.NewGuid(), ReservationStatus.Reserved, spotCode: new string('Ž', 60));

        var ics = CalendarIcsRenderer.BuildSingle(dto, "Dlouhý název parkoviště", Prague);

        Assert.That(ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(System.Text.Encoding.UTF8.GetByteCount), Is.All.LessThanOrEqualTo(75));
    }

    private static ReservationDto CreateDto(
        Guid id,
        ReservationStatus status,
        int sequence = 0,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        string spotCode = "D3-12")
    {
        var from = start ?? Created.AddDays(1);
        return new ReservationDto(
            id, Guid.NewGuid(), spotCode, ParkingSpotType.Standard, Guid.NewGuid(),
            from, end ?? from.AddHours(8), status, false, Created,
            null, null, null, sequence, Created.AddMinutes(sequence));
    }
}
