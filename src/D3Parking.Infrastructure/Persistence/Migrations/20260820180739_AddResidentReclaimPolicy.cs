using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResidentReclaimPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "SpotReleases",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<bool>(
                name: "ManualReleasesAreBinding",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "ResidentNoReplacementAction",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "ResidentProtectionDeadlineMode",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ResidentProtectionLeadHours",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ResidentProtectionPreviousDayTime",
                table: "ParkingSettings",
                type: "time",
                nullable: false,
                defaultValue: new TimeOnly(18, 0, 0));

            migrationBuilder.AddColumn<int>(
                name: "ResidentReclaimPolicy",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "SpotReleases");

            migrationBuilder.DropColumn(
                name: "ManualReleasesAreBinding",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ResidentNoReplacementAction",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ResidentProtectionDeadlineMode",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ResidentProtectionLeadHours",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ResidentProtectionPreviousDayTime",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ResidentReclaimPolicy",
                table: "ParkingSettings");
        }
    }
}
