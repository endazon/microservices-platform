using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationService.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAbacManagementFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-09: ポリシー・属性辞書の更新時刻を保持
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Policies",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "AttributeDefinitions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "AttributeDefinitions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // FR-09: 同一スコープ内でのキー一意制約
            migrationBuilder.CreateIndex(
                name: "IX_AttributeDefinitions_Key_Scope",
                table: "AttributeDefinitions",
                columns: new[] { "Key", "Scope" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttributeDefinitions_Key_Scope",
                table: "AttributeDefinitions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "AttributeDefinitions");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AttributeDefinitions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Policies");
        }
    }
}
