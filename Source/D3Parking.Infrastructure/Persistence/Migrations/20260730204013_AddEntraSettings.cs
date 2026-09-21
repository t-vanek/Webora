using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntraSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EntraSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClientSecretProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Authority = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CallbackPath = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SignedOutCallbackPath = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LinkByVerifiedEmail = table.Column<bool>(type: "bit", nullable: false),
                    AllowJustInTimeProvisioning = table.Column<bool>(type: "bit", nullable: false),
                    DefaultRoles = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScimEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ScimBearerTokenProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScimBlockOnDeprovision = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntraSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntraSettings");
        }
    }
}
