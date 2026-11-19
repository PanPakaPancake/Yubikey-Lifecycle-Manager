using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AgentUpdatePolicy",
                table: "installation",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AutoUpdate",
                table: "agents",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateError",
                table: "agents",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentUpdatePolicy",
                table: "installation");

            migrationBuilder.DropColumn(
                name: "AutoUpdate",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "UpdateError",
                table: "agents");
        }
    }
}
