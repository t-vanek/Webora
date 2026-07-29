using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApologyVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApologyVouchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceMismatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RedeemedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RedeemedReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WaivedCredits = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApologyVouchers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApologyVouchers_RedeemedReservationId",
                table: "ApologyVouchers",
                column: "RedeemedReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApologyVouchers_UserId_RedeemedAtUtc_ExpiresAtUtc",
                table: "ApologyVouchers",
                columns: new[] { "UserId", "RedeemedAtUtc", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApologyVouchers");
        }
    }
}
