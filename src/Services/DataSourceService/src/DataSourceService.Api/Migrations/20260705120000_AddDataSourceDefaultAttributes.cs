using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataSourceService.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceDefaultAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-01, FR-05: 原本へ付与する既定 ABAC 属性。既存行は空 jsonb を既定値とする。
            migrationBuilder.AddColumn<string>(
                name: "DefaultAttributes",
                table: "DataSources",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultAttributes",
                table: "DataSources");
        }
    }
}
