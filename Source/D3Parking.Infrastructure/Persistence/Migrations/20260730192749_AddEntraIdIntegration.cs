using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace D3Parking.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEntraIdIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalObjectId",
                table: "AspNetUsers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalProvider",
                table: "AspNetUsers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExternalSyncedAtUtc",
                table: "AspNetUsers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTenantId",
                table: "AspNetUsers",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExternalRoleAssignments",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalRoleAssignments", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_ExternalRoleAssignments_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExternalRoleAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalRoleMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExternalRole = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalRoleMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalRoleMappings_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ExternalProvider_ExternalObjectId",
                table: "AspNetUsers",
                columns: new[] { "ExternalProvider", "ExternalObjectId" },
                unique: true,
                filter: "[ExternalObjectId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalRoleAssignments_RoleId",
                table: "ExternalRoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalRoleMappings_Provider_ExternalRole",
                table: "ExternalRoleMappings",
                columns: new[] { "Provider", "ExternalRole" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalRoleMappings_RoleId",
                table: "ExternalRoleMappings",
                column: "RoleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalRoleAssignments");

            migrationBuilder.DropTable(
                name: "ExternalRoleMappings");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ExternalProvider_ExternalObjectId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ExternalObjectId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ExternalProvider",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ExternalSyncedAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ExternalTenantId",
                table: "AspNetUsers");
        }
    }
}
