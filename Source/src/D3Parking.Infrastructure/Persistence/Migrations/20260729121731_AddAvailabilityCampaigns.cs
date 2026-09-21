using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAvailabilityCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-tuned defaults: the singleton settings row and every existing preference row
            // must inherit the intended behaviour (campaigns on with the recommended knobs, tips
            // allowed — it is an opt-out), not CLR zeros.
            migrationBuilder.AddColumn<bool>(
                name: "AvailabilityCampaignsEnabled",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "AvailabilityFreeThresholdPercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 25);

            migrationBuilder.AddColumn<int>(
                name: "AvailabilityLookaheadDays",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<int>(
                name: "AvailabilityMinConsecutiveDays",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "AvailabilitySendHourLocal",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 9);

            migrationBuilder.AddColumn<bool>(
                name: "AllowAvailability",
                table: "NotificationPreferences",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AvailabilityCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    OccupancyPercent = table.Column<int>(type: "int", nullable: false),
                    RecipientCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvailabilityCampaigns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityCampaigns_CreatedAtUtc",
                table: "AvailabilityCampaigns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityCampaigns_PeriodStart_PeriodEnd",
                table: "AvailabilityCampaigns",
                columns: new[] { "PeriodStart", "PeriodEnd" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AvailabilityCampaigns");

            migrationBuilder.DropColumn(
                name: "AvailabilityCampaignsEnabled",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AvailabilityFreeThresholdPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AvailabilityLookaheadDays",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AvailabilityMinConsecutiveDays",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AvailabilitySendHourLocal",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AllowAvailability",
                table: "NotificationPreferences");
        }
    }
}
