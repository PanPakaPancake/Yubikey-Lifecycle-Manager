using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeyScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "Scopes",
                table: "api_keys",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            // Bisherige Schlüssel behalten genau, was sie konnten: alles Lesende, ToDo schreiben nur wenn erlaubt.
            // Das Audit-Log war bisher nicht über die API lesbar und bleibt aus.
            migrationBuilder.Sql("""
                UPDATE api_keys SET "Scopes" = CASE WHEN "AllowTodoWrite"
                    THEN ARRAY['inventory.read','assignments.read','reports.read','todos.read','todos.write']
                    ELSE ARRAY['inventory.read','assignments.read','reports.read','todos.read'] END;
                """);

            migrationBuilder.DropColumn(
                name: "AllowTodoWrite",
                table: "api_keys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowTodoWrite",
                table: "api_keys",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""UPDATE api_keys SET "AllowTodoWrite" = 'todos.write' = ANY("Scopes");""");

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "api_keys");
        }
    }
}
