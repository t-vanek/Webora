using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReputationDecay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReputationDecayIntervalDays",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "ReputationDecayPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastDecayUtc",
                table: "ParkerScores",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReputationDecayIntervalDays",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ReputationDecayPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "LastDecayUtc",
                table: "ParkerScores");
        }
    }
}
