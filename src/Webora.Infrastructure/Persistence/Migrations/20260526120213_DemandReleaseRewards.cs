using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DemandReleaseRewards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DemandReleaseOccupancyPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "DemandReleaseQueueBonus",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "MaxReleaseReward",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 40);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DemandReleaseOccupancyPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "DemandReleaseQueueBonus",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "MaxReleaseReward",
                table: "ParkingSettings");
        }
    }
}
