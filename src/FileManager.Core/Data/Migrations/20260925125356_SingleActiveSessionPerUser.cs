using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileManager.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SingleActiveSessionPerUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sessions_UserId",
                table: "sessions");

            migrationBuilder.AddColumn<string>(
                name: "RevokedReason",
                table: "sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_UserId",
                table: "sessions",
                column: "UserId",
                unique: true,
                filter: "\"RevokedUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sessions_UserId",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "RevokedReason",
                table: "sessions");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_UserId",
                table: "sessions",
                column: "UserId");
        }
    }
}
