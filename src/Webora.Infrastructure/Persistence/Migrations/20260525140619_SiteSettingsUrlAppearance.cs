using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SiteSettingsUrlAppearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LowercaseUrls",
                table: "SiteSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TrailingSlash",
                table: "SiteSettings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "NoPreference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LowercaseUrls",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "TrailingSlash",
                table: "SiteSettings");
        }
    }
}
