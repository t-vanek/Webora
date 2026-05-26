using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StreaksAndTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StreakBonusCap",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<int>(
                name: "StreakBonusPerLevel",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "TierGoldPoints",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 150);

            migrationBuilder.AddColumn<int>(
                name: "TierPlatinumPoints",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<int>(
                name: "TierSilverPoints",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "CompletionStreak",
                table: "ParkerScores",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StreakBonusCap",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "StreakBonusPerLevel",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "TierGoldPoints",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "TierPlatinumPoints",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "TierSilverPoints",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "CompletionStreak",
                table: "ParkerScores");
        }
    }
}
