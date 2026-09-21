using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AttributeSharedReservationsToResident : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SharedByResidentId",
                table: "Reservations",
                type: "uniqueidentifier",
                nullable: true);

            // Preserve the provider of historical shared bookings where a matching release record
            // still exists. New bookings capture this directly and no longer depend on current spot ownership.
            migrationBuilder.Sql("""
                UPDATE reservation
                SET [SharedByResidentId] = provider.[OwnerId]
                FROM [Reservations] AS reservation
                CROSS APPLY
                (
                    SELECT TOP (1) release.[OwnerId]
                    FROM [SpotReleases] AS release
                    WHERE release.[SpotId] = reservation.[SpotId]
                      AND release.[Date] >= CAST(reservation.[StartUtc] AS date)
                      AND release.[Date] <= CAST(DATEADD(second, -1, reservation.[EndUtc]) AS date)
                    ORDER BY release.[Date]
                ) AS provider
                WHERE reservation.[UserId] <> provider.[OwnerId];
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_SharedByResidentId_Status",
                table: "Reservations",
                columns: new[] { "SharedByResidentId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reservations_SharedByResidentId_Status",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "SharedByResidentId",
                table: "Reservations");
        }
    }
}
