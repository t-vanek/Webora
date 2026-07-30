using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequireVoucherApprovalAndPhotoProof : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAtUtc",
                table: "ApologyVouchers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedById",
                table: "ApologyVouchers",
                type: "uniqueidentifier",
                nullable: true);

            // Vouchers that exist at upgrade time were granted under the old trust-based rule;
            // grandfathering them in as approved keeps them usable instead of stranding them in
            // a review queue nobody asked for.
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "ApologyVouchers",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Approved");

            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                table: "ApologyVouchers",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MismatchPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MismatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ContentHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    SizeBytes = table.Column<int>(type: "int", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MismatchPhotos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApologyVouchers_SourceMismatchId",
                table: "ApologyVouchers",
                column: "SourceMismatchId");

            migrationBuilder.CreateIndex(
                name: "IX_MismatchPhotos_ContentHash",
                table: "MismatchPhotos",
                column: "ContentHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MismatchPhotos_MismatchId",
                table: "MismatchPhotos",
                column: "MismatchId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MismatchPhotos");

            migrationBuilder.DropIndex(
                name: "IX_ApologyVouchers_SourceMismatchId",
                table: "ApologyVouchers");

            migrationBuilder.DropColumn(
                name: "ReviewedAtUtc",
                table: "ApologyVouchers");

            migrationBuilder.DropColumn(
                name: "ReviewedById",
                table: "ApologyVouchers");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ApologyVouchers");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ApologyVouchers");
        }
    }
}
