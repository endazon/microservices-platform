using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WikiService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSyncMarkAppliedSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AppliedSequence",
                table: "SyncMarks",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedSequence",
                table: "SyncMarks");
        }
    }
}
