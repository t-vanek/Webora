using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <summary>Lets a case wait on the driver's answer, and gives the driver somewhere to answer.</summary>
    public partial class AddOversightParticipantWait : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OversightInfoDeadlineDays",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AwaitingSinceUtc",
                table: "OversightCases",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OversightCases_ReporterId_OpenedAtUtc",
                table: "OversightCases",
                columns: new[] { "ReporterId", "OpenedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing downgraded into knows what "AwaitingInfo" means, and a status the code cannot
            // read is a case nobody can move. Put them back on the desk before the column goes.
            migrationBuilder.Sql("""
                UPDATE [OversightCases]
                SET [Status] = 'InProgress', [AwaitingSinceUtc] = NULL
                WHERE [Status] = 'AwaitingInfo';
                """);

            migrationBuilder.DropIndex(
                name: "IX_OversightCases_ReporterId_OpenedAtUtc",
                table: "OversightCases");

            migrationBuilder.DropColumn(
                name: "OversightInfoDeadlineDays",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "AwaitingSinceUtc",
                table: "OversightCases");
        }
    }
}
