using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Persistence;
using KnowledgePlatform.Shared.Contracts.Dtos;
using KnowledgePlatform.Shared.Contracts.Events;
using KnowledgePlatform.Shared.Infrastructure.Foundation.Extensions;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Api.Foundation.Endpoints;

// FR-06, UC-03: 文書 CRUD・バージョン管理・メタデータ管理エンドポイント
public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // 読み取り（一覧・個別・版）は一般利用者の文書閲覧（SC-03）のためロールで塞がない。
        // 読み取りの機密制御は取得段の ABAC（IADR-0012）が担う。
        var g = app.MapGroup("/documents").WithTags("Documents");

        // FR-06, FR-09, UC-03, IADR-0044: 多層防御。文書の書き込み（作成・更新・メタデータ・公開・
        // アーカイブ・削除）は管理者・運用者に限定する（[[IADR-0041]] の BFF write ゲートと同一要件）。
        // BFF 迂回の直接呼び出しでも認可を実効化する（サービスが最終防衛線）。利用者トークンは BFF が伝播する。
        var write = app.MapGroup("/documents").WithTags("Documents")
            .RequireAuthorization(p => p.RequireRole(
                KnowledgePlatformAuthPolicies.AdminRole,
                KnowledgePlatformAuthPolicies.OperatorRole));

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

        write.MapPost("/", async (CreateDocumentRequest req, DocumentDbContext db,
            IPublishEndpoint bus) =>
        {
            // FR-06, UC-03: タイトルは必須
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["タイトルは必須です。"]
                });

            // FR-05, UC-03, SC-05, IADR-0047: 機密区分（必須属性）のサーバー側検証（最終防衛線）。
            // 欠落・未知値は保存拒否（400）。フロントの既定値に依存せず、BFF 迂回でも実効化する。
            if (ConfidentialityProblemOrNull(req.Attributes) is { } createError)
                return createError;

            var doc = Document.Create(req.Title, req.OriginalUri, req.ContentType,
                req.Attributes, req.Tags);
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            await bus.Publish(ToEvent(doc));
            return Results.Created($"/documents/{doc.Id}", ToDto(doc));
        });

        write.MapPut("/{id:guid}", async (Guid id, UpdateDocumentRequest req,
            DocumentDbContext db, IPublishEndpoint bus) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["タイトルは必須です。"]
                });

            // FR-05, UC-03, SC-05, IADR-0047: 更新でも機密区分を必須検証する（属性は全置換のため）。
            if (ConfidentialityProblemOrNull(req.Attributes) is { } updateError)
                return updateError;

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
        write.MapPatch("/{id:guid}/metadata", async (Guid id, UpdateMetadataRequest req,
            DocumentDbContext db, IPublishEndpoint bus) =>
        {
            // FR-05, UC-03, SC-05, IADR-0047: メタデータ更新も属性を全置換するため機密区分を必須検証する。
            if (ConfidentialityProblemOrNull(req.Attributes) is { } metaError)
                return metaError;

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

        // FR-06, UC-03, SC-05: 文書を公開する。アーカイブ済みからの再公開は不正遷移として 409 で拒否する。
        write.MapPost("/{id:guid}/publish", async (Guid id, DocumentDbContext db,
            IPublishEndpoint bus) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            if (!doc.CanPublish)
                return Results.Conflict(new
                {
                    error = "invalid_transition",
                    from = doc.Status,
                    to = DocumentStatus.Published
                });
            doc.Publish();
            await db.SaveChangesAsync();
            await bus.Publish(ToEvent(doc));
            return Results.Ok(ToDto(doc));
        });

        // FR-06, UC-03, Issue #88: 文書をアーカイブ（非公開化）する。下流の Wiki.js 同期が
        // status=archived を受けてページを非公開化・メタデータ Archived 化する。
        write.MapPost("/{id:guid}/archive", async (Guid id, DocumentDbContext db,
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

        write.MapDelete("/{id:guid}", async (Guid id, DocumentDbContext db,
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

    // FR-05, UC-03, SC-05, IADR-0047: 機密区分（必須属性）検証。NG のとき 400 の IResult を、
    // 妥当なとき null を返す（呼び出し側は `is { } error` で早期リターンする）。
    private static IResult? ConfidentialityProblemOrNull(Dictionary<string, string>? attributes)
    {
        var (ok, error) = DocumentAttributes.ValidateConfidentiality(attributes);
        return ok
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [DocumentAttributes.ConfidentialityKey] = [error!]
            });
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
