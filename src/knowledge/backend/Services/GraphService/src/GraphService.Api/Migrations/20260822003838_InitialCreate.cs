using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "edge_types",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Layer = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsSymmetric = table.Column<bool>(type: "boolean", nullable: false),
                    IsSeed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edge_types", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "graph_documents",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Attributes = table.Column<string>(type: "jsonb", nullable: false),
                    BodyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_documents", x => x.DocumentId);
                });

            migrationBuilder.CreateTable(
                name: "edges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EdgeTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provenance = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceAnchor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    TargetAnchor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_edges_edge_types_EdgeTypeId",
                        column: x => x.EdgeTypeId,
                        principalTable: "edge_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_edge_types_name",
                table: "edge_types",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_edges_source",
                table: "edges",
                column: "SourceDocumentId");

            migrationBuilder.CreateIndex(
                name: "ix_edges_target",
                table: "edges",
                column: "TargetDocumentId");

            migrationBuilder.CreateIndex(
                name: "ix_edges_type",
                table: "edges",
                column: "EdgeTypeId");

            migrationBuilder.CreateIndex(
                name: "ux_edges",
                table: "edges",
                columns: new[] { "SourceDocumentId", "TargetDocumentId", "EdgeTypeId", "SourceAnchor", "TargetAnchor" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edges");

            migrationBuilder.DropTable(
                name: "graph_documents");

            migrationBuilder.DropTable(
                name: "edge_types");
        }
    }
}
