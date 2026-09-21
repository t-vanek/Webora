using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupportMultipleResidentsPerSpot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CompanyVehicles_AssignedSpotId",
                table: "CompanyVehicles");

            migrationBuilder.AddColumn<int>(
                name: "ResidentCapacity",
                table: "ParkingSpots",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                table: "ParkingSpots",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ParkingSpotResidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlannedUseDays = table.Column<int>(type: "int", nullable: false),
                    AutoReleaseUnplannedDays = table.Column<bool>(type: "bit", nullable: false),
                    PlanAppliedThrough = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingSpotResidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParkingSpotResidents_ParkingSpots_SpotId",
                        column: x => x.SpotId,
                        principalTable: "ParkingSpots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpotDayAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    AssignedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpotDayAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpotDayAssignments_ParkingSpotResidents_ResidentId",
                        column: x => x.ResidentId,
                        principalTable: "ParkingSpotResidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SpotDayAssignments_ParkingSpots_SpotId",
                        column: x => x.SpotId,
                        principalTable: "ParkingSpots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Preserve every existing resident and their standing usage plan. OwnerId remains as a
            // compatibility pointer to the first resident while all new operations use this table.
            migrationBuilder.Sql("""
                INSERT INTO [ParkingSpotResidents]
                    ([Id], [SpotId], [UserId], [AssignedAtUtc], [RemovedAtUtc],
                     [PlannedUseDays], [AutoReleaseUnplannedDays], [PlanAppliedThrough])
                SELECT NEWID(), [Id], [OwnerId], SYSUTCDATETIME(), NULL,
                       [PlannedUseDays], [AutoReleaseUnplannedDays], [PlanAppliedThrough]
                FROM [ParkingSpots]
                WHERE [OwnerId] IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyVehicles_AssignedSpotId",
                table: "CompanyVehicles",
                column: "AssignedSpotId",
                filter: "[AssignedSpotId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpotResidents_SpotId_RemovedAtUtc",
                table: "ParkingSpotResidents",
                columns: new[] { "SpotId", "RemovedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpotResidents_SpotId_UserId",
                table: "ParkingSpotResidents",
                columns: new[] { "SpotId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpotResidents_UserId",
                table: "ParkingSpotResidents",
                column: "UserId",
                unique: true,
                filter: "[RemovedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SpotDayAssignments_ResidentId_Date",
                table: "SpotDayAssignments",
                columns: new[] { "ResidentId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_SpotDayAssignments_SpotId_Date",
                table: "SpotDayAssignments",
                columns: new[] { "SpotId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpotDayAssignments");

            migrationBuilder.DropTable(
                name: "ParkingSpotResidents");

            migrationBuilder.DropIndex(
                name: "IX_CompanyVehicles_AssignedSpotId",
                table: "CompanyVehicles");

            migrationBuilder.DropColumn(
                name: "ResidentCapacity",
                table: "ParkingSpots");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ParkingSpots");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyVehicles_AssignedSpotId",
                table: "CompanyVehicles",
                column: "AssignedSpotId",
                unique: true,
                filter: "[AssignedSpotId] IS NOT NULL");
        }
    }
}
