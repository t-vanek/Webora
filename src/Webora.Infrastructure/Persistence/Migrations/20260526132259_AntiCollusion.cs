using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AntiCollusion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AntiCollusionEnabled",
                table: "ParkingSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "CollusionConcentrationPercent",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 70);

            migrationBuilder.AddColumn<int>(
                name: "CollusionMinInteractions",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<int>(
                name: "CollusionScanIntervalHours",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCollusionScanUtc",
                table: "ParkingSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxPairTrustWeight",
                table: "ParkingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.CreateTable(
                name: "CollusionFlags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserA = table.Column<Guid>(type: "uuid", nullable: false),
                    UserB = table.Column<Guid>(type: "uuid", nullable: false),
                    MutualInteractions = table.Column<int>(type: "integer", nullable: false),
                    ConcentrationAPercent = table.Column<int>(type: "integer", nullable: false),
                    ConcentrationBPercent = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DetectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollusionFlags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollusionFlags_Status",
                table: "CollusionFlags",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CollusionFlags_UserA_UserB",
                table: "CollusionFlags",
                columns: new[] { "UserA", "UserB" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollusionFlags");

            migrationBuilder.DropColumn(
                name: "AntiCollusionEnabled",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "CollusionConcentrationPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "CollusionMinInteractions",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "CollusionScanIntervalHours",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "LastCollusionScanUtc",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "MaxPairTrustWeight",
                table: "ParkingSettings");
        }
    }
}
