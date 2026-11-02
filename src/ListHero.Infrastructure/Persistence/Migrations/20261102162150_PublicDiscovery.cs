using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListHero.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicDisplayName",
                table: "Users",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicHandle",
                table: "Users",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Users",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "IX_WishLists_CreatedAt_Id",
                table: "WishLists",
                columns: new[] { "CreatedAt", "Id" },
                descending: new[] { true, false },
                filter: "[IsPublic] = 1 AND [ArchivedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_PublicHandle",
                table: "Users",
                column: "PublicHandle",
                unique: true,
                filter: "[PublicHandle] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WishLists_CreatedAt_Id",
                table: "WishLists");

            migrationBuilder.DropIndex(
                name: "IX_Users_PublicHandle",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PublicDisplayName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PublicHandle",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Users");
        }
    }
}
