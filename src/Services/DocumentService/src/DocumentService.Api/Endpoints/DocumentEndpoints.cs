using DocumentService.Api.Domain;
using DocumentService.Api.Infrastructure;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Api.Endpoints;

// FR-06, UC-03: 文書 CRUD エンドポイント
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/documents").WithTags("Documents");

        g.MapGet("/", async (DocumentDbContext db) =>
        {
            var docs = await db.Documents
                .OrderByDescending(d => d.UpdatedAt)
                .Select(d => ToDto(d))
                .ToListAsync();
            return Results.Ok(docs);
        });

        g.MapGet("/{id:guid}", async (Guid id, DocumentDbContext db) =>
        {
            var doc = await db.Documents.FindAsync(id);
            return doc is null ? Results.NotFound() : Results.Ok(ToDto(doc));
        });

        g.MapPost("/", async (CreateDocumentRequest req, DocumentDbContext db,
            IPublishEndpoint bus) =>
        {
            var doc = Document.Create(req.Title, req.OriginalUri, req.ContentType,
                req.Attributes, req.Tags);
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            await bus.Publish(ToEvent(doc));
            return Results.Created($"/documents/{doc.Id}", ToDto(doc));
        });

        g.MapPut("/{id:guid}", async (Guid id, UpdateDocumentRequest req,
            DocumentDbContext db, IPublishEndpoint bus) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            doc.Update(req.Title, req.Attributes ?? [], req.Tags ?? []);
            await db.SaveChangesAsync();
            await bus.Publish(ToEvent(doc));
            return Results.Ok(ToDto(doc));
        });

        g.MapDelete("/{id:guid}", async (Guid id, DocumentDbContext db) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            db.Documents.Remove(doc);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }

    private static DocumentDto ToDto(Document d) => new()
    {
        Id = d.Id,
        Title = d.Title,
        Status = d.Status,
        MarkdownUri = d.MarkdownUri,
        Attributes = d.Attributes,
        Tags = d.Tags,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };

    // FR-06, UC-03: DocumentUpdated イベント生成
    private static DocumentUpdated ToEvent(Document d) => new(
        d.Id, d.Title, d.Status, d.MarkdownUri,
        d.Attributes, d.Tags, d.UpdatedAt);
}

public record CreateDocumentRequest(
    string Title,
    string? OriginalUri,
    string? ContentType,
    Dictionary<string, string>? Attributes,
    List<string>? Tags);

public record UpdateDocumentRequest(
    string Title,
    Dictionary<string, string>? Attributes,
    List<string>? Tags);
