using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResidentUsagePlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoReleaseUnplannedDays",
                table: "ParkingSpots",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PlanAppliedThrough",
                table: "ParkingSpots",
                type: "date",
                nullable: true);

            // 31 = Weekday.Workdays (Mon–Fri): existing residents keep their spot on the days a
            // commuter needs it, so enabling the plan later frees the weekend, not the whole week.
            migrationBuilder.AddColumn<int>(
                name: "PlannedUseDays",
                table: "ParkingSpots",
                type: "int",
                nullable: false,
                defaultValue: 31);

            // The existing settings row must get the real horizon, not 0 — clamped to 1 it would
            // let plans reach only tomorrow.
            migrationBuilder.AddColumn<int>(
                name: "ResidentPlanHorizonDays",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 14);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoReleaseUnplannedDays",
                table: "ParkingSpots");

            migrationBuilder.DropColumn(
                name: "PlanAppliedThrough",
                table: "ParkingSpots");

            migrationBuilder.DropColumn(
                name: "PlannedUseDays",
                table: "ParkingSpots");

            migrationBuilder.DropColumn(
                name: "ResidentPlanHorizonDays",
                table: "ParkingSettings");
        }
    }
}
