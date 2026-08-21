using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHighOccupancyCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AvailabilityCampaigns_CreatedAtUtc",
                table: "AvailabilityCampaigns");

            migrationBuilder.DropIndex(
                name: "IX_AvailabilityCampaigns_PeriodStart_PeriodEnd",
                table: "AvailabilityCampaigns");

            migrationBuilder.AddColumn<int>(
                name: "AvailabilityBusyThresholdPercent",
                table: "ParkingSettings",
                type: "int",
                nullable: false,
                defaultValue: 85);

            migrationBuilder.AddColumn<bool>(
                name: "HighOccupancyCampaignsEnabled",
                table: "ParkingSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CampaignDate",
                table: "AvailabilityCampaigns",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "AvailabilityCampaigns",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "LowOccupancy");

            migrationBuilder.Sql("""
                UPDATE [AvailabilityCampaigns]
                SET [Kind] = N'LowOccupancy',
                    [CampaignDate] = CONVERT(date, [CreatedAtUtc]);

                WITH [RankedCampaigns] AS
                (
                    SELECT [Id], ROW_NUMBER() OVER
                        (PARTITION BY [Kind], [CampaignDate] ORDER BY [CreatedAtUtc] DESC, [Id] DESC) AS [RowNumber]
                    FROM [AvailabilityCampaigns]
                )
                DELETE FROM [RankedCampaigns] WHERE [RowNumber] > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityCampaigns_Kind_CampaignDate",
                table: "AvailabilityCampaigns",
                columns: new[] { "Kind", "CampaignDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityCampaigns_Kind_PeriodStart_PeriodEnd",
                table: "AvailabilityCampaigns",
                columns: new[] { "Kind", "PeriodStart", "PeriodEnd" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AvailabilityCampaigns_Kind_CampaignDate",
                table: "AvailabilityCampaigns");

            migrationBuilder.DropIndex(
                name: "IX_AvailabilityCampaigns_Kind_PeriodStart_PeriodEnd",
                table: "AvailabilityCampaigns");

            migrationBuilder.DropColumn(
                name: "AvailabilityBusyThresholdPercent",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "HighOccupancyCampaignsEnabled",
                table: "ParkingSettings");

            migrationBuilder.DropColumn(
                name: "CampaignDate",
                table: "AvailabilityCampaigns");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AvailabilityCampaigns");

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityCampaigns_CreatedAtUtc",
                table: "AvailabilityCampaigns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityCampaigns_PeriodStart_PeriodEnd",
                table: "AvailabilityCampaigns",
                columns: new[] { "PeriodStart", "PeriodEnd" });
        }
    }
}
