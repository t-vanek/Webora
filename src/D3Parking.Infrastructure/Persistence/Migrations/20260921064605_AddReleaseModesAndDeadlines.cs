using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseModesAndDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReleaseDeadline",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReleaseLeadMinutes",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 120);

            migrationBuilder.AddColumn<int>(
                name: "ReleaseMode",
                table: "ParkingSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ReleasePreviousDayTime",
                table: "ParkingSettings",
                type: "time",
                nullable: false,
                defaultValue: new TimeOnly(18, 0, 0));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReleaseDeadline",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ReleaseLeadMinutes",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ReleaseMode",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "ReleasePreviousDayTime",
                table: "ParkingSettings");
        }
    }
}
