using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocalUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                table: "platform_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntil",
                table: "platform_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LoginKind",
                table: "platform_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "platform_users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SetupTokenExpiresAt",
                table: "platform_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetupTokenHash",
                table: "platform_users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotpLastStep",
                table: "platform_users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecretProtected",
                table: "platform_users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "WebAuthnUserHandle",
                table: "platform_users",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EntraSsoEnabled",
                table: "installation",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "user_passkeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<byte[]>(type: "bytea", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    SignCount = table.Column<long>(type: "bigint", nullable: false),
                    Aaguid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_passkeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_passkeys_platform_users_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalTable: "platform_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_SetupTokenHash",
                table: "platform_users",
                column: "SetupTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_WebAuthnUserHandle",
                table: "platform_users",
                column: "WebAuthnUserHandle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_passkeys_CredentialId",
                table: "user_passkeys",
                column: "CredentialId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_passkeys_PlatformUserId",
                table: "user_passkeys",
                column: "PlatformUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_passkeys");

            migrationBuilder.DropIndex(
                name: "IX_platform_users_SetupTokenHash",
                table: "platform_users");

            migrationBuilder.DropIndex(
                name: "IX_platform_users_WebAuthnUserHandle",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "LoginKind",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "SetupTokenExpiresAt",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "SetupTokenHash",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "TotpLastStep",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "TotpSecretProtected",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "WebAuthnUserHandle",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "EntraSsoEnabled",
                table: "installation");
        }
    }
}
