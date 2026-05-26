using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ParkingTunables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoVerifyHomeAddress",
                table: "ParkingSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoVerifyMaxDistanceKm",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxReleaseRangeDays",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxRewardedReleasesPerDay",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoVerifyHomeAddress",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AutoVerifyMaxDistanceKm",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "MaxReleaseRangeDays",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "MaxRewardedReleasesPerDay",
                table: "ParkingSettings");
        }
    }
}
