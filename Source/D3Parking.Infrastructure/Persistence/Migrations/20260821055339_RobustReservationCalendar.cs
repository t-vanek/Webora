using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RobustReservationCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CountsTowardWeeklyLimit",
                table: "Reservations",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AlterColumn<int>(
                name: "LastMinuteUnlimitedHours",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 24);

            migrationBuilder.AddColumn<string>(
                name: "HolidayCalendarRegion",
                table: "ParkingSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "CzechRepublic");

            migrationBuilder.AddColumn<bool>(
                name: "PublicHolidayReservationsAllowed",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE [ParkingSettings] SET [LastMinuteUnlimitedHours] = 0, " +
                "[ResidentPlanHorizonDays] = [ReservationHorizonDays];");

            // Preserve the documented resident entitlement for existing data. Shared-pool rows keep
            // the default true; an own-space booking is identifiable by the current owner/membership
            // and by not being attributed to another resident's released capacity.
            migrationBuilder.Sql("""
                UPDATE reservation
                SET [CountsTowardWeeklyLimit] = 0
                FROM [Reservations] AS reservation
                INNER JOIN [ParkingSpots] AS spot ON spot.[Id] = reservation.[SpotId]
                WHERE reservation.[SharedByResidentId] IS NULL
                  AND (spot.[OwnerId] = reservation.[UserId]
                       OR EXISTS (
                           SELECT 1
                           FROM [ParkingSpotResidents] AS membership
                           WHERE membership.[SpotId] = reservation.[SpotId]
                             AND membership.[UserId] = reservation.[UserId]
                             AND membership.[RemovedAtUtc] IS NULL));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountsTowardWeeklyLimit",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "HolidayCalendarRegion",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "PublicHolidayReservationsAllowed",
                table: "ParkingSettings");

            migrationBuilder.AlterColumn<int>(
                name: "LastMinuteUnlimitedHours",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 24,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);
        }
    }
}
