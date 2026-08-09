using DocumentService.Api.Foundation.Domain;
using DocumentService.Api.Foundation.Persistence;
using DocumentService.Api.Foundation.Services;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Extensions;
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

        // FR-06, FR-09, UC-03, IADR-0044: 多層防御。BFF 迂回の直接呼び出しでも認可を実効化する
        // （サービスが最終防衛線）。利用者トークンは BFF が伝播する。
        //
        // **［#629］このグループ既定は「閲覧の下限」であり、書き込みの実効境界ではない。**
        // 計画 §SC-05「管理系 3 画面の閲覧ロール」（裁定 Q19）は
        // **閲覧を管理者・運用者へ開き、破壊的操作は管理者限定を維持する**と定めている。
        // したがって**個々の書き込み口へ `AdminOnly` を積む**（AND 合成で実効 admin のみ）。
        // **既定を `AdminOnly` へ置き換えないのは、この行が閲覧の下限を表しているからである**
        // （[[IADR-0128]] 決定 1 が #501 で確立し、#628 が踏襲した形）。
        var write = app.MapGroup("/documents").WithTags("Documents")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        g.MapGet("/", async (DocumentDbContext db) =>
        {
            var names = await TagResolver.NamesAsync(db);
            var docs = await db.Documents
                .OrderByDescending(d => d.UpdatedAt)
                .ToListAsync();
            return Results.Ok(docs.Select(d => ToDto(d, names)).ToList());
        });

        g.MapGet("/{id:guid}", async (Guid id, DocumentDbContext db) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            return Results.Ok(ToDto(doc, await TagResolver.NamesAsync(db)));
        });

        // FR-06, UC-03, SC-05（#629）: **ここだけ `AdminOnly` を積んでいない。据え置きである。**
        //
        // 計画の列挙は「登録」を破壊的操作に含めるが、**この口は人間の画面だけの口ではない**——
        // `ai-stock-trading` の KB 書き込み（AST/FR-08）が `HttpKnowledgeBaseWriter` から
        // **BFF を経由せず直接**叩いており、その service-account
        // （`ai-stock-trading-kb-writer`）は **`platform-operator` しか持たない**。
        // [[IADR-0075]] が最小権限を理由に `platform-admin` の付与を**明示的に却下している**ためである。
        //
        // したがって `AdminOnly` を積むと **AST の KB 書き込みが 403 で止まる**（実測で確認）。
        // 計画の Q19 は SC-05 の**画面と人間のロール**についての裁定であり、
        // **機械クライアントの扱いを述べていない**。実装側で決めずに計画へ裁定を依頼した
        // （環流記録 `20260809_document-write-machine-client.md`）。
        //
        // **人間の運用者に対する実効境界は BFF 側で閉じている**——`/bff/documents` の `POST` は
        // `AdminOnly` であり、DocumentService はメッシュ内部でイングレス非公開である。
        // **裁定が出たらここを追随させる。**
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

            // SC-05, #635: タグ名を辞書の識別子へ解決する。**辞書に無い名前は 400**（手入力は自動登録しない）。
            var (createTagIds, createUnknown) = await TagResolver.ToIdsAsync(db, req.Tags);
            if (createUnknown.Count > 0) return UnknownTagsProblem(createUnknown);

            var doc = Document.Create(req.Title, req.OriginalUri, req.ContentType,
                req.Attributes, createTagIds);
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            var createNames = await TagResolver.NamesAsync(db);
            await bus.Publish(ToEvent(doc, createNames));
            return Results.Created($"/documents/{doc.Id}", ToDto(doc, createNames));
        });

        // FR-06, UC-03, SC-05（#629）: 編集は**管理者限定**（計画の列挙「文書の編集」）。
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

            var (updateTagIds, updateUnknown) = await TagResolver.ToIdsAsync(db, req.Tags);
            if (updateUnknown.Count > 0) return UnknownTagsProblem(updateUnknown);

            doc.Update(req.Title, req.Attributes ?? [], updateTagIds, req.ChangeNote);
            await db.SaveChangesAsync();
            var updateNames = await TagResolver.NamesAsync(db);
            await bus.Publish(ToEvent(doc, updateNames));
            return Results.Ok(ToDto(doc, updateNames));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-06, UC-03: メタデータ（属性・タグ）のみ更新する。
        // FR-06, UC-03, SC-05（#629）: メタデータ更新も**管理者限定**（計画の列挙「更新」「文書の編集」）。
        // **BFF にこの口は無い**（実測。射程は「狭める」なので、ここで足さない）。
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

            var (metaTagIds, metaUnknown) = await TagResolver.ToIdsAsync(db, req.Tags);
            if (metaUnknown.Count > 0) return UnknownTagsProblem(metaUnknown);

            doc.UpdateMetadata(req.Attributes ?? [], metaTagIds, req.ChangeNote);
            await db.SaveChangesAsync();
            var metaNames = await TagResolver.NamesAsync(db);
            await bus.Publish(ToEvent(doc, metaNames));
            return Results.Ok(ToDto(doc, metaNames));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-06, UC-03, SC-05: 文書を公開する。アーカイブ済みからの再公開は不正遷移として 409 で拒否する。
        // FR-06, UC-03, SC-05（#629）: 公開は**管理者限定**。
        //
        // **計画の破壊的操作の列挙に名前が無い**ため、planning#299 が新設した基準を当てはめた。
        // 同基準の例外（運用者へ開く）は **2 条件を同時に満たすとき**であり、
        // 唯一の適用例である手動同期（SC-06）は (a) 既存データを壊さない ＋
        // (b) **運用者が SC-10 で異常に気づいたその場で一次対応できる**の両方を満たしていた。
        // **公開は (b) を満たさない** —— 異常への一次対応ではなく、公開範囲を決める統制行為である。
        // **計画が名指しした例外は手動同期ただ 1 つ**なので、名指しの無い操作の既定は一般則
        // （破壊的操作は管理者限定）に従う。判断の全文は作業仕様書 §判断 1。
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
            var names = await TagResolver.NamesAsync(db);
            await bus.Publish(ToEvent(doc, names));
            return Results.Ok(ToDto(doc, names));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-06, UC-03, Issue #88: 文書をアーカイブ（非公開化）する。下流の Wiki.js 同期が
        // status=archived を受けてページを非公開化・メタデータ Archived 化する。
        // FR-06, UC-03, SC-05（#629）: アーカイブは**管理者限定**。公開と同じ基準で分類した
        // （作業仕様書 §判断 1）。**アーカイブは (a) すら満たさない** —— 下流の Wiki.js 同期が
        // ページを非公開化するため、**可視性を落とす**。「既存データを壊さない」とは言い切れない。
        write.MapPost("/{id:guid}/archive", async (Guid id, DocumentDbContext db,
            IPublishEndpoint bus) =>
        {
            var doc = await db.Documents.FindAsync(id);
            if (doc is null) return Results.NotFound();
            doc.Archive();
            await db.SaveChangesAsync();
            var names = await TagResolver.NamesAsync(db);
            await bus.Publish(ToEvent(doc, names));
            return Results.Ok(ToDto(doc, names));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-06, UC-03: 版履歴一覧（新しい順）。
        g.MapGet("/{id:guid}/versions", async (Guid id, DocumentDbContext db) =>
        {
            var exists = await db.Documents.AnyAsync(d => d.Id == id);
            if (!exists) return Results.NotFound();

            var names = await TagResolver.NamesAsync(db);
            var versions = await db.DocumentVersions
                .Where(v => v.DocumentId == id)
                .OrderByDescending(v => v.Version)
                .ToListAsync();
            return Results.Ok(versions.Select(v => ToVersionDto(v, names)).ToList());
        });

        // FR-06, UC-03: 特定版の取得。
        g.MapGet("/{id:guid}/versions/{version:int}", async (Guid id, int version,
            DocumentDbContext db) =>
        {
            var snapshot = await db.DocumentVersions
                .FirstOrDefaultAsync(v => v.DocumentId == id && v.Version == version);
            if (snapshot is null) return Results.NotFound();
            return Results.Ok(ToVersionDto(snapshot, await TagResolver.NamesAsync(db)));
        });

        // FR-06, UC-03, SC-05（#629）: 削除は**管理者限定**（計画の列挙「文書の削除」）。
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
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);

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

    // FR-09, SC-09, #635: **外へ出す形は表示名である**（正本は識別子。[[IADR-0153]] 決定 2）。
    // 契約（`DocumentDto.Tags`）は `List<string>` のままで、**下流も画面も変わらない**。
    private static DocumentDto ToDto(Document d, IReadOnlyDictionary<Guid, string> names) => new()
    {
        Id = d.Id,
        Title = d.Title,
        Status = d.Status,
        MarkdownUri = d.MarkdownUri,
        Version = d.Version,
        Attributes = d.Attributes,
        Tags = TagResolver.ToNames(d.Tags, names),
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };

    // **過去版も現在の表示名で出る**——改名は表示上の変更である（[[IADR-0153]] 決定 4）。
    private static DocumentVersionDto ToVersionDto(DocumentVersion v, IReadOnlyDictionary<Guid, string> names) => new()
    {
        DocumentId = v.DocumentId,
        Version = v.Version,
        Title = v.Title,
        Status = v.Status,
        MarkdownUri = v.MarkdownUri,
        Attributes = v.Attributes,
        Tags = TagResolver.ToNames(v.Tags, names),
        ChangeNote = v.ChangeNote,
        CreatedAt = v.CreatedAt,
    };

    // FR-06, UC-03: DocumentUpdated イベント生成
    // **イベントも表示名を運ぶ。** 射影（Qdrant / Wiki.js）は人が読む面であり、
    // 検索の hot path に辞書引きを増やさない（[[IADR-0153]] 決定 1・2）。
    //
    // **［#635］`internal` にしてある。** 改名の再発行（`TagDictionaryEndpoints`）が同じ形を要るためで、
    // **識別子 → 表示名の変換点を 2 つに割らない**ことがここでの目的である（同 決定 2）。
    internal static DocumentUpdated ToEvent(Document d, IReadOnlyDictionary<Guid, string> names) => new(
        d.Id, d.Title, d.Status, d.MarkdownUri,
        d.Attributes, TagResolver.ToNames(d.Tags, names), d.UpdatedAt);

    // SC-05, #635: 辞書に無いタグ名を 400 にする（「既定タグ辞書に整合」。手入力は自動登録しない）。
    private static IResult UnknownTagsProblem(List<string> unknown) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["tags"] = [$"辞書に無いタグです: {string.Join(" / ", unknown)}。SC-09 の辞書へ先に登録してください。"],
        });
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
