using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommuteDistance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "LotLatitude",
                table: "ParkingSettings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LotLongitude",
                table: "ParkingSettings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SharedTakenBasePoints",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SharedTakenMaxMultiplier",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SharedTakenReferenceKm",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "CommuteDistanceKm",
                table: "AspNetUsers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HomeAddress",
                table: "AspNetUsers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HomeLatitude",
                table: "AspNetUsers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HomeLongitude",
                table: "AspNetUsers",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LotLatitude",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "LotLongitude",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "SharedTakenBasePoints",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "SharedTakenMaxMultiplier",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "SharedTakenReferenceKm",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "CommuteDistanceKm",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "HomeAddress",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "HomeLatitude",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "HomeLongitude",
                table: "AspNetUsers");
        }
    }
}
