using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeuralImageBackup.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _005_KeptDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KeptDuplicates",
                columns: table => new
                {
                    ImageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeptDuplicates", x => x.ImageId);
                    table.ForeignKey(
                        name: "FK_KeptDuplicates_Images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "Images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KeptDuplicates");
        }
    }
}
