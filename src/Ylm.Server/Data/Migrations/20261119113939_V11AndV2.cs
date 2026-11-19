using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class V11AndV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "NinjaOrganizationId",
                table: "tenants",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NinjaOrganizationName",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireBreakglassApproval",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Custodian",
                table: "keys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IdentifiedAt",
                table: "keys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Usage",
                table: "keys",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AllowTodoWrite",
                table: "api_keys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "integration_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    WebhookAuditCursor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ninja_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    BaseUrl = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    ClientSecretProtected = table.Column<string>(type: "text", nullable: true),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    FieldMap = table.Column<string>(type: "jsonb", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastRunMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ninja_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "registration_approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Usage = table.Column<int>(type: "integer", nullable: false),
                    EntraUserIds = table.Column<List<string>>(type: "text[]", nullable: false),
                    UserPrincipalNames = table.Column<List<string>>(type: "text[]", nullable: false),
                    Custodian = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedById = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByName = table.Column<string>(type: "text", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedById = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByName = table.Column<string>(type: "text", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "text", nullable: true),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    KeyId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registration_approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_registration_approvals_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "registration_todos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntraUserId = table.Column<string>(type: "text", nullable: false),
                    UserPrincipalName = table.Column<string>(type: "text", nullable: false),
                    UserDisplayName = table.Column<string>(type: "text", nullable: true),
                    Department = table.Column<string>(type: "text", nullable: true),
                    TicketId = table.Column<string>(type: "text", nullable: true),
                    TicketUrl = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Source = table.Column<string>(type: "text", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByName = table.Column<string>(type: "text", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedByName = table.Column<string>(type: "text", nullable: true),
                    KeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registration_todos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_registration_todos_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Recipients = table.Column<List<string>>(type: "text[]", nullable: false),
                    AllTenants = table.Column<bool>(type: "boolean", nullable: false),
                    TenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                    Hour = table.Column<int>(type: "integer", nullable: false),
                    TimeZone = table.Column<string>(type: "text", nullable: false),
                    IncludeCsv = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "smtp_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Host = table.Column<string>(type: "text", nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Security = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    PasswordProtected = table.Column<string>(type: "text", nullable: true),
                    FromAddress = table.Column<string>(type: "text", nullable: true),
                    FromName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_smtp_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    SecretProtected = table.Column<string>(type: "text", nullable: false),
                    Events = table.Column<List<string>>(type: "text[]", nullable: false),
                    AllTenants = table.Column<bool>(type: "boolean", nullable: false),
                    TenantIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    LastDeliveryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastDeliverySucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhooks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuditSequence = table.Column<long>(type: "bigint", nullable: true),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastStatusCode = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_webhooks_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "webhooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_registration_approvals_TenantId_Status",
                table: "registration_approvals",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_registration_todos_TenantId_EntraUserId",
                table: "registration_todos",
                columns: new[] { "TenantId", "EntraUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_registration_todos_TenantId_Status",
                table: "registration_todos",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_Status_NextAttemptAt",
                table: "webhook_deliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_SubscriptionId_CreatedAt",
                table: "webhook_deliveries",
                columns: new[] { "SubscriptionId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_state");

            migrationBuilder.DropTable(
                name: "ninja_settings");

            migrationBuilder.DropTable(
                name: "registration_approvals");

            migrationBuilder.DropTable(
                name: "registration_todos");

            migrationBuilder.DropTable(
                name: "report_schedules");

            migrationBuilder.DropTable(
                name: "smtp_settings");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "webhooks");

            migrationBuilder.DropColumn(
                name: "NinjaOrganizationId",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "NinjaOrganizationName",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "RequireBreakglassApproval",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "Custodian",
                table: "keys");

            migrationBuilder.DropColumn(
                name: "IdentifiedAt",
                table: "keys");

            migrationBuilder.DropColumn(
                name: "Usage",
                table: "keys");

            migrationBuilder.DropColumn(
                name: "AllowTodoWrite",
                table: "api_keys");
        }
    }
}
