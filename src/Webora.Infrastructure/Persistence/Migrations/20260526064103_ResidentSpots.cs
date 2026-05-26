using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResidentSpots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "LastResidentReminderDate",
                table: "ParkingSpots",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyShareAllowance",
                table: "ParkingSpots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "ParkingSpots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ResidentHoldUntil",
                table: "ParkingSettings",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0));

            migrationBuilder.AddColumn<int>(
                name: "ResidentMaxShareAllowance",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ResidentReleaseMaxPoints",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ResidentReleasePointsPerHour",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ResidentSharePercentPerAllowance",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SpotReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpotId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AwardedPoints = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpotReleases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpots_OwnerId",
                table: "ParkingSpots",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SpotReleases_OwnerId_Date",
                table: "SpotReleases",
                columns: new[] { "OwnerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_SpotReleases_SpotId_Date",
                table: "SpotReleases",
                columns: new[] { "SpotId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpotReleases");

            migrationBuilder.DropIndex(
                name: "IX_ParkingSpots_OwnerId",
                table: "ParkingSpots");

            migrationBuilder.DropColumn(
                name: "LastResidentReminderDate",
                table: "ParkingSpots");

            migrationBuilder.DropColumn(
                name: "MonthlyShareAllowance",
                table: "ParkingSpots");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "ParkingSpots");

            migrationBuilder.DropColumn(
                name: "ResidentHoldUntil",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ResidentMaxShareAllowance",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ResidentReleaseMaxPoints",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ResidentReleasePointsPerHour",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ResidentSharePercentPerAllowance",
                table: "ParkingSettings");
        }
    }
}
