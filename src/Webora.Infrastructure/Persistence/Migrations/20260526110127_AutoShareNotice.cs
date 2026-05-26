using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutoShareNotice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "LastAutoShareNoticeDate",
                table: "ParkingSpots",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAutoShareNoticeDate",
                table: "ParkingSpots");
        }
    }
}
