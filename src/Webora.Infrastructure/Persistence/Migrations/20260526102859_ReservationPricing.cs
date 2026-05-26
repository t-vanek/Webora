using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReservationPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreditsCharged",
                table: "Reservations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BaseReservationCost",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "MaxReservationCost",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 40);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyCreditAllowance",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "OccupancyPricePercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<int>(
                name: "PeakPricePercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<int>(
                name: "Credits",
                table: "ParkerScores",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LastCreditGrantPeriod",
                table: "ParkerScores",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreditsCharged",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "BaseReservationCost",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "MaxReservationCost",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "MonthlyCreditAllowance",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "OccupancyPricePercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "PeakPricePercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "Credits",
                table: "ParkerScores");

            migrationBuilder.DropColumn(
                name: "LastCreditGrantPeriod",
                table: "ParkerScores");
        }
    }
}
