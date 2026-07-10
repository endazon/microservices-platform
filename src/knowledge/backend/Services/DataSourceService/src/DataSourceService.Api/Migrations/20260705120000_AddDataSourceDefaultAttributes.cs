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
            // FR-01, FR-05, IADR-0019: 原本へ付与する既定 ABAC 属性。既存行は confidentiality=internal を
            // backfill し、マイグレーション前に登録済みのデータソースが機密区分欠落で fail-closed 除外
            // （IADR-0012）されるのを防ぐ。発行時の GetEffectiveAttributes() が最終防衛線。
            migrationBuilder.AddColumn<string>(
                name: "DefaultAttributes",
                table: "DataSources",
                type: "jsonb",
                nullable: false,
                defaultValue: "{\"confidentiality\":\"internal\"}");
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
