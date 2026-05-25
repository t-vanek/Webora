using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Webora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SiteSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    Scheme = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: true),
                    ForceHttps = table.Column<bool>(type: "boolean", nullable: false),
                    HstsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    HstsMaxAgeDays = table.Column<int>(type: "integer", nullable: false),
                    HstsIncludeSubDomains = table.Column<bool>(type: "boolean", nullable: false),
                    WwwPreference = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Aliases = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteSettings");
        }
    }
}
