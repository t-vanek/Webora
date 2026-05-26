using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ParkingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ParkingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleasePoints = table.Column<int>(type: "integer", nullable: false),
                    OffPeakBonusPoints = table.Column<int>(type: "integer", nullable: false),
                    NoShowPenaltyPoints = table.Column<int>(type: "integer", nullable: false),
                    ReleaseCutoff = table.Column<TimeSpan>(type: "interval", nullable: false),
                    NoShowGracePeriod = table.Column<TimeSpan>(type: "interval", nullable: false),
                    ReminderLeadTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    PeakStart = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    PeakEnd = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    SweepInterval = table.Column<TimeSpan>(type: "interval", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParkingSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParkingSettings");
        }
    }
}
