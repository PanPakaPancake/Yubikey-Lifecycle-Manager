using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RolesAndPartnerStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomRoleId",
                table: "platform_users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "keys",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "custom_roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Permissions = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_roles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_CustomRoleId",
                table: "platform_users",
                column: "CustomRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_keys_partner_serial",
                table: "keys",
                column: "SerialNumber",
                unique: true,
                filter: "\"TenantId\" IS NULL AND \"SerialNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_custom_roles_Name",
                table: "custom_roles",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_platform_users_custom_roles_CustomRoleId",
                table: "platform_users",
                column: "CustomRoleId",
                principalTable: "custom_roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_platform_users_custom_roles_CustomRoleId",
                table: "platform_users");

            migrationBuilder.DropTable(
                name: "custom_roles");

            migrationBuilder.DropIndex(
                name: "IX_platform_users_CustomRoleId",
                table: "platform_users");

            migrationBuilder.DropIndex(
                name: "IX_keys_partner_serial",
                table: "keys");

            migrationBuilder.DropColumn(
                name: "CustomRoleId",
                table: "platform_users");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "keys",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
