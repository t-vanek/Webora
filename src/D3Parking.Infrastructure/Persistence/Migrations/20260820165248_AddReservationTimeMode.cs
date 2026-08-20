using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationTimeMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReservationTimeMode",
                table: "ParkingSettings",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "TimeWindow");

            // Peak pricing and off-peak rewards are retired. Preserve the legacy columns for
            // historical reports, but neutralize their active configuration on deployment.
            migrationBuilder.Sql("""
                UPDATE [ParkingSettings]
                SET [OffPeakBonusPoints] = 0,
                    [PeakPricePercent] = 100,
                    [AdaptivePricingEnabled] = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReservationTimeMode",
                table: "ParkingSettings");
        }
    }
}
