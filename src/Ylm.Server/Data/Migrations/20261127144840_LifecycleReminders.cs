using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RemindLeavers",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RemindNoSpareKey",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RemindRetiredWithPasskey",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RemindUnusedKeys",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ReminderCreateTodos",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UnusedKeyDays",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUsedAt",
                table: "key_registrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UserAccountEnabled",
                table: "key_registrations",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reminder_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DismissedByName = table.Column<string>(type: "text", nullable: true),
                    DismissNote = table.Column<string>(type: "text", nullable: true),
                    TodoId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reminder_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reminder_states_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_states_TenantId_Kind_Subject",
                table: "reminder_states",
                columns: new[] { "TenantId", "Kind", "Subject" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reminder_states");

            migrationBuilder.DropColumn(
                name: "RemindLeavers",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "RemindNoSpareKey",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "RemindRetiredWithPasskey",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "RemindUnusedKeys",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ReminderCreateTodos",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "UnusedKeyDays",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "key_registrations");

            migrationBuilder.DropColumn(
                name: "UserAccountEnabled",
                table: "key_registrations");
        }
    }
}
