using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Persistence;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Api.Foundation.Endpoints;

// FR-06, UC-03: 文書 CRUD・バージョン管理・メタデータ管理エンドポイント
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
            // FR-06, UC-03: タイトルは必須
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["タイトルは必須です。"]
                });

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
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["タイトルは必須です。"]
                });

            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();

            // FR-06, UC-03: 楽観的並行制御。期待版が現在版と異なれば lost update を防ぐため 409。
            if (req.ExpectedVersion is { } expected && expected != doc.Version)
                return Results.Conflict(new
                {
                    error = "version_conflict",
                    expectedVersion = expected,
                    currentVersion = doc.Version
                });

            doc.Update(req.Title, req.Attributes ?? [], req.Tags ?? [], req.ChangeNote);
            await db.SaveChangesAsync();
            await bus.Publish(ToEvent(doc));
            return Results.Ok(ToDto(doc));
        });

        // FR-06, UC-03: メタデータ（属性・タグ）のみ更新する。
        g.MapPatch("/{id:guid}/metadata", async (Guid id, UpdateMetadataRequest req,
            DocumentDbContext db, IPublishEndpoint bus) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();

            if (req.ExpectedVersion is { } expected && expected != doc.Version)
                return Results.Conflict(new
                {
                    error = "version_conflict",
                    expectedVersion = expected,
                    currentVersion = doc.Version
                });

            doc.UpdateMetadata(req.Attributes ?? [], req.Tags ?? [], req.ChangeNote);
            await db.SaveChangesAsync();
            await bus.Publish(ToEvent(doc));
            return Results.Ok(ToDto(doc));
        });

        // FR-06, UC-03: 文書を公開する。
        g.MapPost("/{id:guid}/publish", async (Guid id, DocumentDbContext db,
            IPublishEndpoint bus) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            doc.Publish();
            await db.SaveChangesAsync();
            await bus.Publish(ToEvent(doc));
            return Results.Ok(ToDto(doc));
        });

        // FR-06, UC-03, Issue #88: 文書をアーカイブ（非公開化）する。下流の Wiki.js 同期が
        // status=archived を受けてページを非公開化・メタデータ Archived 化する。
        g.MapPost("/{id:guid}/archive", async (Guid id, DocumentDbContext db,
            IPublishEndpoint bus) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            doc.Archive();
            await db.SaveChangesAsync();
            await bus.Publish(ToEvent(doc));
            return Results.Ok(ToDto(doc));
        });

        // FR-06, UC-03: 版履歴一覧（新しい順）。
        g.MapGet("/{id:guid}/versions", async (Guid id, DocumentDbContext db) =>
        {
            var exists = await db.Documents.AnyAsync(d => d.Id == id);
            if (!exists) return Results.NotFound();

            var versions = await db.DocumentVersions
                .Where(v => v.DocumentId == id)
                .OrderByDescending(v => v.Version)
                .Select(v => ToVersionDto(v))
                .ToListAsync();
            return Results.Ok(versions);
        });

        // FR-06, UC-03: 特定版の取得。
        g.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version,
            DocumentDbContext db) =>
        {
            var snapshot = await db.DocumentVersions
                .FirstOrDefaultAsync(v => v.DocumentId == id && v.Version == version);
            return snapshot is null ? Results.NotFound() : Results.Ok(ToVersionDto(snapshot));
        });

        g.MapDelete("/{id:guid}", async (Guid id, DocumentDbContext db,
            IPublishEndpoint bus) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            db.Documents.Remove(doc);
            await db.SaveChangesAsync();
            // Issue #88: 削除を下流（Wiki.js 同期）へ伝播し、外部システムの実体を撤去する。
            await bus.Publish(new DocumentDeleted(id, DateTimeOffset.UtcNow));
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
        Version = d.Version,
        Attributes = d.Attributes,
        Tags = d.Tags,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };

    private static DocumentVersionDto ToVersionDto(DocumentVersion v) => new()
    {
        DocumentId = v.DocumentId,
        Version = v.Version,
        Title = v.Title,
        Status = v.Status,
        MarkdownUri = v.MarkdownUri,
        Attributes = v.Attributes,
        Tags = v.Tags,
        ChangeNote = v.ChangeNote,
        CreatedAt = v.CreatedAt,
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
    List<string>? Tags,
    int? ExpectedVersion = null,
    string? ChangeNote = null);

// FR-06, UC-03: メタデータ（属性・タグ）のみ更新するリクエスト
public record UpdateMetadataRequest(
    Dictionary<string, string>? Attributes,
    List<string>? Tags,
    int? ExpectedVersion = null,
    string? ChangeNote = null);
