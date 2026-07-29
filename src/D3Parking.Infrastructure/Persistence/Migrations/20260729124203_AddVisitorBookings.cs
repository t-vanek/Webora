using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitorBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VisitorBookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitorName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Company = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LicensePlate = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    HostUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitorBookings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisitorBookings_SpotId_StartUtc",
                table: "VisitorBookings",
                columns: new[] { "SpotId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VisitorBookings_StartUtc",
                table: "VisitorBookings",
                column: "StartUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VisitorBookings");
        }
    }
}
