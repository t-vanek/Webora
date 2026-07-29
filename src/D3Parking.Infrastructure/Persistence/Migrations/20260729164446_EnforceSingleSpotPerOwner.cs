using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleSpotPerOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParkingSpots_OwnerId",
                table: "ParkingSpots");

            // The service used to allow assigning one resident to several spots, so existing data
            // may violate the new invariant and would fail the index build. Keep each owner's
            // first spot by code (the one the resident flows most likely resolved) and return the
            // rest to the shared pool.
            migrationBuilder.Sql("""
                WITH Ranked AS (
                    SELECT Id, ROW_NUMBER() OVER (PARTITION BY OwnerId ORDER BY Code) AS Rank
                    FROM ParkingSpots
                    WHERE OwnerId IS NOT NULL
                )
                UPDATE s SET s.OwnerId = NULL
                FROM ParkingSpots s
                JOIN Ranked r ON r.Id = s.Id
                WHERE r.Rank > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpots_OwnerId",
                table: "ParkingSpots",
                column: "OwnerId",
                unique: true,
                filter: "[OwnerId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ParkingSpots_OwnerId",
                table: "ParkingSpots");

            migrationBuilder.CreateIndex(
                name: "IX_ParkingSpots_OwnerId",
                table: "ParkingSpots",
                column: "OwnerId");
        }
    }
}
