using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorType = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "text", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    KeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Target = table.Column<string>(type: "text", nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    IpAddress = table.Column<string>(type: "text", nullable: true),
                    PreviousHash = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "installation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    OrganizationName = table.Column<string>(type: "text", nullable: false),
                    PublicBaseUrl = table.Column<string>(type: "text", nullable: true),
                    HomeTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    CertificateThumbprint = table.Column<string>(type: "text", nullable: true),
                    CertificateExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BootstrapOwnerUpn = table.Column<string>(type: "text", nullable: true),
                    SetupCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_installation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pairing_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    PlatformUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pairing_codes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntraObjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Upn = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    AllTenants = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntraTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    DefaultDomain = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsentedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsentedBy = table.Column<string>(type: "text", nullable: true),
                    TargetGroupId = table.Column<string>(type: "text", nullable: true),
                    TargetGroupName = table.Column<string>(type: "text", nullable: true),
                    SyncIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastSyncMessage = table.Column<string>(type: "text", nullable: true),
                    TargetUserCount = table.Column<int>(type: "integer", nullable: true),
                    LastCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastChecksJson = table.Column<string>(type: "jsonb", nullable: true),
                    PolicySnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    OffboardedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DataDeletionDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    AgentVersion = table.Column<string>(type: "text", nullable: true),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agents_platform_users_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalTable: "platform_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Prefix = table.Column<string>(type: "text", nullable: false),
                    SecretHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_keys_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "custom_field_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Options = table.Column<List<string>>(type: "text[]", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_definitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_definitions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SerialNumber = table.Column<int>(type: "integer", nullable: true),
                    FirmwareVersion = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    FormFactor = table.Column<string>(type: "text", nullable: true),
                    AaGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    IsFips = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LockReason = table.Column<int>(type: "integer", nullable: true),
                    StatusNote = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    CustomFields = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    PinChangeForced = table.Column<bool>(type: "boolean", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_keys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_keys_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "platform_user_tenants",
                columns: table => new
                {
                    PlatformUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_user_tenants", x => new { x.PlatformUserId, x.TenantId });
                    table.ForeignKey(
                        name: "FK_platform_user_tenants_platform_users_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalTable: "platform_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_platform_user_tenants_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "registration_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    PinStrategy = table.Column<int>(type: "integer", nullable: false),
                    PinLength = table.Column<int>(type: "integer", nullable: false),
                    FixedPinProtected = table.Column<string>(type: "text", nullable: true),
                    ForcePinChange = table.Column<bool>(type: "boolean", nullable: false),
                    FactoryResetBeforeRegistration = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayNameTemplate = table.Column<string>(type: "text", nullable: false),
                    AllowedModels = table.Column<List<string>>(type: "text[]", nullable: false),
                    MinimumFirmware = table.Column<string>(type: "text", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registration_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_registration_profiles_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "key_registrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntraUserId = table.Column<string>(type: "text", nullable: false),
                    UserPrincipalName = table.Column<string>(type: "text", nullable: false),
                    UserDisplayName = table.Column<string>(type: "text", nullable: true),
                    EntraMethodId = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    AaGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RegisteredById = table.Column<Guid>(type: "uuid", nullable: true),
                    RegisteredByName = table.Column<string>(type: "text", nullable: true),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    LastSeenInSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_key_registrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_key_registrations_keys_KeyId",
                        column: x => x.KeyId,
                        principalTable: "keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agents_PlatformUserId",
                table: "agents",
                column: "PlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_Prefix",
                table: "api_keys",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_TenantId",
                table: "api_keys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_KeyId",
                table: "audit_log",
                column: "KeyId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_Sequence",
                table: "audit_log",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_TenantId_Timestamp",
                table: "audit_log",
                columns: new[] { "TenantId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_definitions_TenantId_Key",
                table: "custom_field_definitions",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_key_registrations_KeyId",
                table: "key_registrations",
                column: "KeyId");

            migrationBuilder.CreateIndex(
                name: "IX_key_registrations_TenantId_EntraMethodId",
                table: "key_registrations",
                columns: new[] { "TenantId", "EntraMethodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_key_registrations_TenantId_UserPrincipalName",
                table: "key_registrations",
                columns: new[] { "TenantId", "UserPrincipalName" });

            migrationBuilder.CreateIndex(
                name: "IX_keys_TenantId_SerialNumber",
                table: "keys",
                columns: new[] { "TenantId", "SerialNumber" },
                unique: true,
                filter: "\"SerialNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_keys_TenantId_Status",
                table: "keys",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_pairing_codes_CodeHash",
                table: "pairing_codes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_user_tenants_TenantId",
                table: "platform_user_tenants",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_EntraObjectId",
                table: "platform_users",
                column: "EntraObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_users_Upn",
                table: "platform_users",
                column: "Upn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_registration_profiles_TenantId",
                table: "registration_profiles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_EntraTenantId",
                table: "tenants",
                column: "EntraTenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "custom_field_definitions");

            migrationBuilder.DropTable(
                name: "installation");

            migrationBuilder.DropTable(
                name: "key_registrations");

            migrationBuilder.DropTable(
                name: "pairing_codes");

            migrationBuilder.DropTable(
                name: "platform_user_tenants");

            migrationBuilder.DropTable(
                name: "registration_profiles");

            migrationBuilder.DropTable(
                name: "keys");

            migrationBuilder.DropTable(
                name: "platform_users");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
