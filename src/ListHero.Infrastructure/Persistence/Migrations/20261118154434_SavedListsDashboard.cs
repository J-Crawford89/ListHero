using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ListHero.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SavedListsDashboard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WishListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShareLinkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedLists_ListShareLinks_ShareLinkId",
                        column: x => x.ShareLinkId,
                        principalTable: "ListShareLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SavedLists_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SavedLists_WishLists_WishListId",
                        column: x => x.WishListId,
                        principalTable: "WishLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedLists_ShareLinkId",
                table: "SavedLists",
                column: "ShareLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedLists_UserId_WishListId",
                table: "SavedLists",
                columns: new[] { "UserId", "WishListId" },
                unique: true,
                filter: "[ArchivedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SavedLists_WishListId",
                table: "SavedLists",
                column: "WishListId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedLists");
        }
    }
}
