using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixEdgeTypeWeightDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "Weight",
                table: "edge_types",
                type: "double precision",
                nullable: false,
                defaultValue: 0.5,
                oldClrType: typeof(double),
                oldType: "double precision");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "Weight",
                table: "edge_types",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision",
                oldDefaultValue: 0.5);
        }
    }
}
