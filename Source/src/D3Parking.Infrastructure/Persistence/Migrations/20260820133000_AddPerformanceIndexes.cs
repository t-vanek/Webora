using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations;

/// <summary>Indexes for the queue matcher and the dashboard's interval analytics.</summary>
[DbContext(typeof(D3ParkingDbContext))]
[Migration("20260820133000_AddPerformanceIndexes")]
public partial class AddPerformanceIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
                name: "IX_PointsLedgerEntries_Reason_OccurredAtUtc",
                table: "PointsLedgerEntries",
                columns: new[] { "Reason", "OccurredAtUtc" });

        migrationBuilder.CreateIndex(
                name: "IX_QueueEntries_Status_StartUtc",
                table: "QueueEntries",
                columns: new[] { "Status", "StartUtc" })
            .Annotation("SqlServer:Include", new[] { "EndUtc", "OfferedSpotId", "OfferExpiresAtUtc" });

        migrationBuilder.CreateIndex(
                name: "IX_Reservations_Status_StartUtc",
                table: "Reservations",
                columns: new[] { "Status", "StartUtc" })
            .Annotation("SqlServer:Include", new[] { "EndUtc", "SpotId", "UserId" });

        migrationBuilder.CreateIndex(
                name: "IX_Reservations_UserId_Status_StartUtc",
                table: "Reservations",
                columns: new[] { "UserId", "Status", "StartUtc" })
            .Annotation("SqlServer:Include", new[] { "EndUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_PointsLedgerEntries_Reason_OccurredAtUtc", table: "PointsLedgerEntries");
        migrationBuilder.DropIndex(name: "IX_QueueEntries_Status_StartUtc", table: "QueueEntries");
        migrationBuilder.DropIndex(name: "IX_Reservations_Status_StartUtc", table: "Reservations");
        migrationBuilder.DropIndex(name: "IX_Reservations_UserId_Status_StartUtc", table: "Reservations");
    }
}
