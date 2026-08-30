using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConversionService.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadLetteredMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeadLettered",
                table: "ConversionJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadLettered",
                table: "ConversionJobs");
        }
    }
}
