using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOversightSlaAndTriage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OversightCases_SpotId",
                table: "OversightCases");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastOversightDigestUtc",
                table: "ParkingSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OversightDigestHourLocal",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<int>(
                name: "OversightRecurrenceThreshold",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "OversightRecurrenceWindowDays",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "OversightSlaCriticalHours",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<int>(
                name: "OversightSlaHighHours",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<int>(
                name: "OversightSlaLowHours",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 168);

            migrationBuilder.AddColumn<int>(
                name: "OversightSlaNormalHours",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 72);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DueAtUtc",
                table: "OversightCases",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SlaBreachedAtUtc",
                table: "OversightCases",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OversightCases_SpotId_OpenedAtUtc",
                table: "OversightCases",
                columns: new[] { "SpotId", "OpenedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OversightCases_Status_DueAtUtc",
                table: "OversightCases",
                columns: new[] { "Status", "DueAtUtc" });

            BackfillDeadlines(migrationBuilder);
        }

        /// <summary>
        /// Gives the cases that predate deadlines one, so the overdue view is not blind to
        /// everything already on the desk. A case whose new deadline already sits in the past is
        /// marked as announced without a timeline entry and without a notification: it breached
        /// nothing at the time — the rule is new — so announcing it retroactively would invent
        /// history, and doing it for the whole backlog at once would open with a burst of alerts
        /// nobody can act on.
        /// </summary>
        private static void BackfillDeadlines(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                WITH [Sla] AS (
                    SELECT c.[Id],
                           DATEADD(hour,
                               CASE c.[Priority]
                                   WHEN 'Critical' THEN ISNULL(s.[OversightSlaCriticalHours], 4)
                                   WHEN 'High' THEN ISNULL(s.[OversightSlaHighHours], 24)
                                   WHEN 'Low' THEN ISNULL(s.[OversightSlaLowHours], 168)
                                   ELSE ISNULL(s.[OversightSlaNormalHours], 72)
                               END,
                               c.[OpenedAtUtc]) AS [Due]
                    FROM [OversightCases] c
                    LEFT JOIN [ParkingSettings] s ON s.[Id] = '00000000-0000-0000-0000-0000000000B1'
                    WHERE c.[Status] <> 'Resolved'
                )
                UPDATE c
                SET [DueAtUtc] = sla.[Due],
                    [SlaBreachedAtUtc] = CASE WHEN sla.[Due] <= SYSDATETIMEOFFSET() THEN SYSDATETIMEOFFSET() END
                FROM [OversightCases] c
                JOIN [Sla] sla ON sla.[Id] = c.[Id];
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OversightCases_SpotId_OpenedAtUtc",
                table: "OversightCases");

            migrationBuilder.DropIndex(
                name: "IX_OversightCases_Status_DueAtUtc",
                table: "OversightCases");

            migrationBuilder.DropColumn(
                name: "LastOversightDigestUtc",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "OversightDigestHourLocal",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "OversightRecurrenceThreshold",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "OversightRecurrenceWindowDays",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "OversightSlaCriticalHours",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "OversightSlaHighHours",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "OversightSlaLowHours",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "OversightSlaNormalHours",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "DueAtUtc",
                table: "OversightCases");

            migrationBuilder.DropColumn(
                name: "SlaBreachedAtUtc",
                table: "OversightCases");

            migrationBuilder.CreateIndex(
                name: "IX_OversightCases_SpotId",
                table: "OversightCases",
                column: "SpotId");
        }
    }
}
