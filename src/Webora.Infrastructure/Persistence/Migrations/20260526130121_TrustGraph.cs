using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrustGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastTrustComputeUtc",
                table: "ParkingSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TrustEnabled",
                table: "ParkingSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "TrustIntervalHours",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<int>(
                name: "TrustedBadgeThreshold",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TrustComputedUtc",
                table: "ParkerScores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrustScore",
                table: "ParkerScores",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTrustComputeUtc",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "TrustEnabled",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "TrustIntervalHours",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "TrustedBadgeThreshold",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "TrustComputedUtc",
                table: "ParkerScores");

            migrationBuilder.DropColumn(
                name: "TrustScore",
                table: "ParkerScores");
        }
    }
}
