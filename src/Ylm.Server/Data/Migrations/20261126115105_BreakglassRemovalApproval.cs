using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BreakglassRemovalApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequireBreakglassRemovalApproval",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "registration_approvals",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RegistrationId",
                table: "registration_approvals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemovalAction",
                table: "registration_approvals",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_registration_approvals_KeyId",
                table: "registration_approvals",
                column: "KeyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_registration_approvals_KeyId",
                table: "registration_approvals");

            migrationBuilder.DropColumn(
                name: "RequireBreakglassRemovalApproval",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "registration_approvals");

            migrationBuilder.DropColumn(
                name: "RegistrationId",
                table: "registration_approvals");

            migrationBuilder.DropColumn(
                name: "RemovalAction",
                table: "registration_approvals");
        }
    }
}
