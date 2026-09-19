using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveParkingWorkflowPromises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefundDeadlineUtc",
                table: "Reservations",
                type: "datetimeoffset",
                nullable: true);

            // Older bookings did not snapshot refund terms. Freeze the terms in effect at
            // upgrade, so later settings edits cannot silently change those bookings again.
            migrationBuilder.Sql("""
                UPDATE [Reservations]
                SET [RefundDeadlineUtc] = DATEADD(SECOND,
                    -COALESCE((SELECT TOP (1) DATEDIFF(SECOND, CAST('00:00:00' AS time), [ReleaseCutoff])
                        FROM [ParkingSettings]), 0), [StartUtc])
                WHERE [RefundDeadlineUtc] IS NULL;
                """);

            migrationBuilder.AddColumn<string>(
                name: "RequiredSpotType",
                table: "QueueEntries",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolvedAtUtc",
                table: "OccupancyMismatches",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ResidentDayHolds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResidentDayHolds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResidentDayHolds_ParkingSpots_SpotId",
                        column: x => x.SpotId,
                        principalTable: "ParkingSpots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResidentDayHolds_SpotId_UserId_Date",
                table: "ResidentDayHolds",
                columns: new[] { "SpotId", "UserId", "Date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResidentDayHolds");

            migrationBuilder.DropColumn(
                name: "RefundDeadlineUtc",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "RequiredSpotType",
                table: "QueueEntries");

            migrationBuilder.DropColumn(
                name: "ResolvedAtUtc",
                table: "OccupancyMismatches");
        }
    }
}
