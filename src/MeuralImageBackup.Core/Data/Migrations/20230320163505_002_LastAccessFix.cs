using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeuralImageBackup.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _002_LastAccessFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM LastGalleries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
