using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlannerRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "TrustEnabled",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<int>(
                name: "TierDiscountPercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 5);

            migrationBuilder.AlterColumn<int>(
                name: "TierAllowanceBonus",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 20);

            migrationBuilder.AlterColumn<int>(
                name: "ReputationDecayPercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 10);

            migrationBuilder.AlterColumn<int>(
                name: "QueuePriorityPerTier",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 30);

            migrationBuilder.AlterColumn<int>(
                name: "QueueOfferMinutes",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 30,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 15);

            migrationBuilder.AlterColumn<int>(
                name: "OccupancyPricePercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 100);

            migrationBuilder.AlterColumn<int>(
                name: "MonthlyCreditAllowance",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 100);

            migrationBuilder.AlterColumn<int>(
                name: "MaxReservationCost",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 40);

            migrationBuilder.AlterColumn<int>(
                name: "MaxReleaseReward",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 40);

            migrationBuilder.AlterColumn<int>(
                name: "DemandReleaseQueueBonus",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 5);

            migrationBuilder.AlterColumn<int>(
                name: "DemandReleaseOccupancyPercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 100);

            migrationBuilder.AlterColumn<int>(
                name: "BaseReservationCost",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 10);

            migrationBuilder.AlterColumn<bool>(
                name: "AntiCollusionEnabled",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "LastMinuteUnlimitedHours",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<int>(
                name: "ReservationHorizonDays",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<int>(
                name: "WeeklyReservationLimit",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklyReservationLimitEnabled",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            // Convert the singleton from incentives and live-operation controls to a calm planner.
            // Capacity rules replace variable pricing, status policing and loyalty advantages.
            migrationBuilder.Sql("""
                UPDATE [ParkingSettings]
                SET [ReleasePoints] = 0,
                    [OffPeakBonusPoints] = 0,
                    [NoShowPenaltyPoints] = 0,
                    [ReleaseCutoff] = '00:00:00',
                    [NoShowGracePeriod] = '00:00:00',
                    [ReminderLeadTime] = '00:00:00',
                    [ResidentReleasePointsPerHour] = 0,
                    [ResidentReleaseMaxPoints] = 0,
                    [ResidentMaxShareAllowance] = 0,
                    [ResidentSharePercentPerAllowance] = 0,
                    [ResidentWastedShareClawbackPercent] = 0,
                    [ResidentPlanHorizonDays] = 21,
                    [SharedTakenBasePoints] = 0,
                    [MaxRewardedReleasesPerDay] = 0,
                    [BaseReservationCost] = 0,
                    [OccupancyPricePercent] = 0,
                    [MaxReservationCost] = 0,
                    [MonthlyCreditAllowance] = 0,
                    [QueueOfferMinutes] = 30,
                    [QueueNoShowPenaltyPoints] = 0,
                    [QueueNoShowCreditPenalty] = 0,
                    [QueueNoShowBanDays] = 0,
                    [QueueNoShowAllowancePenalty] = 0,
                    [DemandReleaseOccupancyPercent] = 0,
                    [DemandReleaseQueueBonus] = 0,
                    [MaxReleaseReward] = 0,
                    [StreakBonusPerLevel] = 0,
                    [StreakBonusCap] = 0,
                    [QueuePriorityPerTier] = 0,
                    [TierAllowanceBonus] = 0,
                    [TierDiscountPercent] = 0,
                    [ReputationDecayPercent] = 0,
                    [AdaptivePricingEnabled] = 0,
                    [TrustEnabled] = 0,
                    [AntiCollusionEnabled] = 0,
                    [AvailabilityFreeThresholdPercent] = 60,
                    [AvailabilityMinConsecutiveDays] = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastMinuteUnlimitedHours",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ReservationHorizonDays",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "WeeklyReservationLimit",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "WeeklyReservationLimitEnabled",
                table: "ParkingSettings");

            migrationBuilder.AlterColumn<bool>(
                name: "TrustEnabled",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<int>(
                name: "TierDiscountPercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 5,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "TierAllowanceBonus",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 20,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "ReputationDecayPercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 10,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "QueuePriorityPerTier",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 30,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "QueueOfferMinutes",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 15,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 30);

            migrationBuilder.AlterColumn<int>(
                name: "OccupancyPricePercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 100,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "MonthlyCreditAllowance",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 100,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "MaxReservationCost",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 40,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "MaxReleaseReward",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 40,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "DemandReleaseQueueBonus",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 5,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "DemandReleaseOccupancyPercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 100,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "BaseReservationCost",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 10,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<bool>(
                name: "AntiCollusionEnabled",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: false);
        }
    }
}
