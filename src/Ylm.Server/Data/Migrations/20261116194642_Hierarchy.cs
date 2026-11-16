using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Hierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New columns first, then move the data, then drop the old columns.
            migrationBuilder.AddColumn<string>(
                name: "TargetIncludeGroups",
                table: "tenants",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "TargetExcludeGroups",
                table: "tenants",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            // Single target group -> inclusion list.
            migrationBuilder.Sql("""
                UPDATE tenants
                SET "TargetIncludeGroups" = jsonb_build_array(jsonb_build_object('Id', "TargetGroupId", 'Name', COALESCE("TargetGroupName", "TargetGroupId")))
                WHERE "TargetGroupId" IS NOT NULL AND "TargetGroupId" <> '';
                """);

            migrationBuilder.AddColumn<bool>(
                name: "AllTenants",
                table: "api_keys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<Guid>>(
                name: "TenantIds",
                table: "api_keys",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            // Tenant API keys become platform keys that stay restricted to their tenant.
            migrationBuilder.Sql("""UPDATE api_keys SET "TenantIds" = ARRAY["TenantId"];""");

            migrationBuilder.DropForeignKey(
                name: "FK_api_keys_tenants_TenantId",
                table: "api_keys");

            migrationBuilder.DropIndex(
                name: "IX_custom_field_definitions_TenantId_Key",
                table: "custom_field_definitions");

            migrationBuilder.DropIndex(
                name: "IX_api_keys_TenantId",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "TargetGroupId",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "TargetGroupName",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "api_keys");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "custom_field_definitions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "custom_field_overrides",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: true),
                    Required = table.Column<bool>(type: "boolean", nullable: true),
                    Options = table.Column<List<string>>(type: "text[]", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_overrides", x => new { x.TenantId, x.FieldId });
                    table.ForeignKey(
                        name: "FK_custom_field_overrides_custom_field_definitions_FieldId",
                        column: x => x.FieldId,
                        principalTable: "custom_field_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_custom_field_overrides_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_profile_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<string>(type: "text", nullable: false),
                    GroupName = table.Column<string>(type: "text", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_profile_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_group_profile_assignments_registration_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "registration_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_group_profile_assignments_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_definitions_TenantId_Key",
                table: "custom_field_definitions",
                columns: new[] { "TenantId", "Key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_overrides_FieldId",
                table: "custom_field_overrides",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_group_profile_assignments_ProfileId",
                table: "group_profile_assignments",
                column: "ProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_group_profile_assignments_TenantId_GroupId",
                table: "group_profile_assignments",
                columns: new[] { "TenantId", "GroupId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_field_overrides");

            migrationBuilder.DropTable(
                name: "group_profile_assignments");

            migrationBuilder.DropIndex(
                name: "IX_custom_field_definitions_TenantId_Key",
                table: "custom_field_definitions");

            migrationBuilder.DropColumn(
                name: "TargetExcludeGroups",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "TargetIncludeGroups",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "AllTenants",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "TenantIds",
                table: "api_keys");

            migrationBuilder.AddColumn<string>(
                name: "TargetGroupId",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetGroupName",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "custom_field_definitions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "api_keys",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_definitions_TenantId_Key",
                table: "custom_field_definitions",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_TenantId",
                table: "api_keys",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_api_keys_tenants_TenantId",
                table: "api_keys",
                column: "TenantId",
                principalTable: "tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
