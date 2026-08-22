using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEdgeTypeWeight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Weight",
                table: "edge_types",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Weight",
                table: "edge_types");
        }
    }
}
