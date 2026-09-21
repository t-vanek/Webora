using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyVehicles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyVehicles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Plate = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    NormalizedPlate = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DriverEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AssignedSpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PairedUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PairedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PairingCodeSentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PairingAttempts = table.Column<int>(type: "int", nullable: false),
                    PairingAttemptsWindowStartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyVehicles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyVehicles_AssignedSpotId",
                table: "CompanyVehicles",
                column: "AssignedSpotId",
                unique: true,
                filter: "[AssignedSpotId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyVehicles_NormalizedPlate",
                table: "CompanyVehicles",
                column: "NormalizedPlate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyVehicles_PairedUserId",
                table: "CompanyVehicles",
                column: "PairedUserId",
                unique: true,
                filter: "[PairedUserId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyVehicles");
        }
    }
}
