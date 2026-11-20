using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProtectedAccountsAndCertRenewal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ProtectAdmins",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedGroups",
                table: "tenants",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ApplicationObjectId",
                table: "installation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertificateRenewalAttemptAt",
                table: "installation",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertificateRenewalError",
                table: "installation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CertificateWarnedAt",
                table: "installation",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectAdmins",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ProtectedGroups",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ApplicationObjectId",
                table: "installation");

            migrationBuilder.DropColumn(
                name: "CertificateRenewalAttemptAt",
                table: "installation");

            migrationBuilder.DropColumn(
                name: "CertificateRenewalError",
                table: "installation");

            migrationBuilder.DropColumn(
                name: "CertificateWarnedAt",
                table: "installation");
        }
    }
}
