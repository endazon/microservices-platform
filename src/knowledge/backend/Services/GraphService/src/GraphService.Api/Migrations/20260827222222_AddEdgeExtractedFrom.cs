using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEdgeExtractedFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExtractedFrom",
                table: "edges",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_edges_extracted_from",
                table: "edges",
                column: "ExtractedFrom");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_edges_extracted_from",
                table: "edges");

            migrationBuilder.DropColumn(
                name: "ExtractedFrom",
                table: "edges");
        }
    }
}
