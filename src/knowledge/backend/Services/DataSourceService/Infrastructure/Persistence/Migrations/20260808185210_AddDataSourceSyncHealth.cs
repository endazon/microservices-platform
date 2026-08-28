using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataSourceService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceSyncHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-01, FR-02, UC-04, SC-06（Q14 / #537）, IADR-0148: 同期健全性の永続化。
            // **バックフィルは行わない** —— 連続失敗回数の既定 0 は「まだ失敗していない」であり、
            // 過去の失敗回数は元々どこにも記録されていない（従前はプロセスローカルのインメモリ計数だった）。
            // 0 で始めることは実態と一致する。直近エラーも同じ理由で null 始まりでよい。
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailureCount",
                table: "DataSources",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastSyncError",
                table: "DataSources",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSyncErrorAt",
                table: "DataSources",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsecutiveFailureCount",
                table: "DataSources");

            migrationBuilder.DropColumn(
                name: "LastSyncError",
                table: "DataSources");

            migrationBuilder.DropColumn(
                name: "LastSyncErrorAt",
                table: "DataSources");
        }
    }
}
