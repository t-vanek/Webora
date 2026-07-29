using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CapResidentShareClawback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClawedBackPoints",
                table: "SpotReleases",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClawedBackPoints",
                table: "SpotReleases");
        }
    }
}
