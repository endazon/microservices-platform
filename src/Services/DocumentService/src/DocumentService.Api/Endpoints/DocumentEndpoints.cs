using KnowledgePlatform.Shared.Contracts.Dtos;

namespace DocumentService.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/documents").WithTags("Documents");

        // FR-06, UC-03: 文書 CRUD スケルトン（P1 で永続化を実装）
        group.MapGet("/", () => Results.Ok(Array.Empty<DocumentDto>()))
            .WithName("GetDocuments").Produces<DocumentDto[]>();

        group.MapGet("/{id:guid}", (Guid id) =>
            Results.NotFound(new { message = $"Document {id} not found (stub)" }))
            .WithName("GetDocument").Produces<DocumentDto>().Produces(404);

        group.MapPost("/", (DocumentDto dto) =>
            Results.Accepted($"/documents/{dto.Id}", dto))
            .WithName("CreateDocument").Produces<DocumentDto>(202);

        group.MapPut("/{id:guid}", (Guid id, DocumentDto dto) =>
            Results.Ok(new DocumentDto
            {
                Id = id,
                Title = dto.Title,
                Status = dto.Status,
                MarkdownUri = dto.MarkdownUri,
                Attributes = dto.Attributes,
                Tags = dto.Tags,
                CreatedAt = dto.CreatedAt,
                UpdatedAt = dto.UpdatedAt
            }))
            .WithName("UpdateDocument").Produces<DocumentDto>();

        group.MapDelete("/{id:guid}", (Guid id) =>
            Results.NoContent())
            .WithName("DeleteDocument").Produces(204);
    }
}
