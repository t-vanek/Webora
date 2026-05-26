using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdaptivePricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AdaptiveDeadbandPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "AdaptiveGainPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "AdaptiveIntervalMinutes",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "AdaptivePeakMaxPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 400);

            migrationBuilder.AddColumn<int>(
                name: "AdaptivePeakMinPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<bool>(
                name: "AdaptivePricingEnabled",
                table: "ParkingSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AdaptiveStepMaxPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 25);

            migrationBuilder.AddColumn<int>(
                name: "AdaptiveTargetOccupancyPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 85);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAdaptiveAdjustUtc",
                table: "ParkingSettings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdaptiveDeadbandPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AdaptiveGainPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AdaptiveIntervalMinutes",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AdaptivePeakMaxPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AdaptivePeakMinPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AdaptivePricingEnabled",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AdaptiveStepMaxPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AdaptiveTargetOccupancyPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "LastAdaptiveAdjustUtc",
                table: "ParkingSettings");
        }
    }
}
