using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The first case anybody can open — something wrong with the lot — and the room for a ruling
    /// against a person to be recorded where the rest of the case already is.
    /// </summary>
    public partial class AddSpotDefectsAndSanctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OversightAllowUserReports",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "SpotDefectReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReporterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ReportedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpotDefectReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpotDefectReports_ReportedAtUtc",
                table: "SpotDefectReports",
                column: "ReportedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Their subject is about to stop existing, and nothing downgraded into can read the
            // kind anyway — a case whose evidence and meaning are both gone is not a record, it is
            // a row nobody can act on. Their timelines go with them (cascade).
            migrationBuilder.Sql("DELETE FROM [OversightCases] WHERE [Kind] = 'SpotDefect';");

            migrationBuilder.DropTable(
                name: "SpotDefectReports");

            migrationBuilder.DropColumn(
                name: "OversightAllowUserReports",
                table: "ParkingSettings");
        }
    }
}
