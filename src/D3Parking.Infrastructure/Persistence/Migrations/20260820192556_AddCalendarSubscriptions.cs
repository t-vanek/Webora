using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CalendarSequence",
                table: "Reservations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CalendarUpdatedAtUtc",
                table: "Reservations",
                type: "datetimeoffset",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");

            // Existing reservations have not changed since their creation from the calendar's
            // perspective. This also keeps initial feed ETags deterministic after deployment.
            migrationBuilder.Sql(
                "UPDATE [Reservations] SET [CalendarUpdatedAtUtc] = [CreatedAtUtc]");

            migrationBuilder.CreateTable(
                name: "CalendarSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSubscriptions_TokenHash",
                table: "CalendarSubscriptions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSubscriptions_UserId",
                table: "CalendarSubscriptions",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarSubscriptions");

            migrationBuilder.DropColumn(
                name: "CalendarSequence",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "CalendarUpdatedAtUtc",
                table: "Reservations");
        }
    }
}
