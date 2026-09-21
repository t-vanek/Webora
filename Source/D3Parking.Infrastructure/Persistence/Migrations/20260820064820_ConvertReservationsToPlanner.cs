using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConvertReservationsToPlanner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A deployment can happen while somebody still has a legacy checked-in reservation.
            // Convert it back to the planner's single live state so no departure action is needed.
            migrationBuilder.Sql("""
                UPDATE [Reservations]
                SET [Status] = N'Reserved', [CheckedInAtUtc] = NULL
                WHERE [Status] = N'CheckedIn';
                """);

            // Retire all rules that depended on proving physical presence. Clear existing sanctions
            // as well, so they cannot continue reducing the user's planning budget after deployment.
            migrationBuilder.Sql("""
                UPDATE [ParkingSettings]
                SET [OffPeakBonusPoints] = 0,
                    [NoShowPenaltyPoints] = 0,
                    [NoShowGracePeriod] = '00:00:00',
                    [ResidentMaxShareAllowance] = 0,
                    [ResidentSharePercentPerAllowance] = 0,
                    [ResidentWastedShareClawbackPercent] = 0,
                    [SharedTakenBasePoints] = 0,
                    [StreakBonusPerLevel] = 0,
                    [StreakBonusCap] = 0,
                    [QueueNoShowPenaltyPoints] = 0,
                    [QueueNoShowCreditPenalty] = 0,
                    [QueueNoShowBanDays] = 0,
                    [QueueNoShowAllowancePenalty] = 0;

                UPDATE [ParkingSpots]
                SET [MonthlyShareAllowance] = 0;

                UPDATE [ParkerScores]
                SET [QueueBannedUntilUtc] = NULL,
                    [NextAllowancePenalty] = 0;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "StreakBonusPerLevel",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 2);

            migrationBuilder.AlterColumn<int>(
                name: "StreakBonusCap",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 20);

            migrationBuilder.AlterColumn<int>(
                name: "QueueNoShowPenaltyPoints",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 50);

            migrationBuilder.AlterColumn<int>(
                name: "QueueNoShowCreditPenalty",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 30);

            migrationBuilder.AlterColumn<int>(
                name: "QueueNoShowBanDays",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 14);

            migrationBuilder.AlterColumn<int>(
                name: "QueueNoShowAllowancePenalty",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "StreakBonusPerLevel",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 2,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "StreakBonusCap",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 20,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "QueueNoShowPenaltyPoints",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 50,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "QueueNoShowCreditPenalty",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 30,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "QueueNoShowBanDays",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 14,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "QueueNoShowAllowancePenalty",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 30,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);
        }
    }
}
