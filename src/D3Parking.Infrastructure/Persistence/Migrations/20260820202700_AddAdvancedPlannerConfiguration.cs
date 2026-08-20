using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvancedPlannerConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AllowedReservationWeekdays",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 127);

            migrationBuilder.AddColumn<string>(
                name: "BudgetRenewalPeriod",
                table: "ParkingSettings",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Monthly");

            // Older rows store a monthly YYYYMM key. The generalized scheduler compares a
            // YYYYMMDD period-start key, so preserve the same already-granted month as its first day.
            migrationBuilder.Sql(
                "UPDATE [ParkerScores] SET [LastCreditGrantPeriod] = [LastCreditGrantPeriod] * 100 + 1 " +
                "WHERE [LastCreditGrantPeriod] BETWEEN 100001 AND 999912;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE [ParkerScores] SET [LastCreditGrantPeriod] = [LastCreditGrantPeriod] / 100 " +
                "WHERE [LastCreditGrantPeriod] BETWEEN 10000101 AND 99991231;");

            migrationBuilder.DropColumn(
                name: "AllowedReservationWeekdays",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "BudgetRenewalPeriod",
                table: "ParkingSettings");
        }
    }
}
