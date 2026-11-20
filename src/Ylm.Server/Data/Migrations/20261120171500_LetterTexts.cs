using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ylm.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetterTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LetterTexts",
                table: "brandings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LetterTexts",
                table: "brandings");
        }
    }
}
