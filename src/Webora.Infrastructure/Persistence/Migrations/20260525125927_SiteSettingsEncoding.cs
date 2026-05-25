using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SiteSettingsEncoding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmailCharset",
                table: "SiteSettings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PageCharset",
                table: "SiteSettings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailCharset",
                table: "SiteSettings");

            migrationBuilder.DropColumn(
                name: "PageCharset",
                table: "SiteSettings");
        }
    }
}
