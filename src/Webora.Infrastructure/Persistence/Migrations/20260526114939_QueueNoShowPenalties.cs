using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QueueNoShowPenalties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FromQueue",
                table: "Reservations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "QueueNoShowAllowancePenalty",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "QueueNoShowBanDays",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<int>(
                name: "QueueNoShowCreditPenalty",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "QueueNoShowPenaltyPoints",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "NextAllowancePenalty",
                table: "ParkerScores",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QueueBannedUntilUtc",
                table: "ParkerScores",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FromQueue",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "QueueNoShowAllowancePenalty",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "QueueNoShowBanDays",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "QueueNoShowCreditPenalty",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "QueueNoShowPenaltyPoints",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "NextAllowancePenalty",
                table: "ParkerScores");

            migrationBuilder.DropColumn(
                name: "QueueBannedUntilUtc",
                table: "ParkerScores");
        }
    }
}
