using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TierPerks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QueuePriorityPerTier",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "TierAllowanceBonus",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<int>(
                name: "TierDiscountPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QueuePriorityPerTier",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "TierAllowanceBonus",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "TierDiscountPercent",
                table: "ParkingSettings");
        }
    }
}
