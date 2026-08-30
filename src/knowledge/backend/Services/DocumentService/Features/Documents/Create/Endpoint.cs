using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.Documents.Create;

// FR-06, UC-03, SC-05（#629）: 文書の登録。**ここだけ `AdminOnly` を積んでいない。据え置きである。**
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
// （環流記録 `feedback/20260809_document-write-machine-client.md`。
// 計画側へは PR planning#306 で伝達済み・**裁定待ち**）。
//
// **人間の運用者に対する実効境界は BFF 側で閉じている**——`/bff/documents` の `POST` は
// `AdminOnly` であり、DocumentService はメッシュ内部でイングレス非公開である。
// **裁定が出たらここを追随させる。**
internal static class CreateDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost("/", async (CreateDocumentRequest req, DocumentDbContext db,
            IObjectStorageClient storage, IDocumentUpdatedPublisher bus, HttpContext http,
            CancellationToken ct) =>
        {
            // FR-06, UC-03: タイトルは必須
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["タイトルは必須です。"]
                });

            // FR-21 受け入れ基準 ⑥: 本文が 1 MB を超える登録要求は **413** で拒否する。
            // **切り詰めて成功を返さない**（切り詰めると ⑦「全文が索引される」が静かに破れる）。
            if (!string.IsNullOrEmpty(req.Body) && DocumentBodyIntake.ExceedsLimit(req.Body))
                return DocumentEndpoints.BodyTooLargeProblem();

            // FR-05, UC-03, SC-05, IADR-0047: 機密区分（必須属性）のサーバー側検証（最終防衛線）。
            // 欠落・未知値は保存拒否（400）。フロントの既定値に依存せず、BFF 迂回でも実効化する。
            if (DocumentEndpoints.ConfidentialityProblemOrNull(req.Attributes) is { } createError)
                return createError;

            // FR-19, ADR-0054, [[IADR-0270]] 決定 2: doc_scope の値域検証（未知値は 400）。
            // さらに**一般経路での個人資料の作成を拒否する** —— 台帳（PrivateNote）を持たない
            // 個人資料ができると容量算入（FR-19）から漏れる。作成経路は /private-notes と
            // /private-notes/sync に限る。
            if (DocumentEndpoints.DocScopeProblemOrNull(req.Attributes) is { } createScopeError)
                return createScopeError;
            if (DocumentAttributes.IsPrivateNote(req.Attributes))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [DocumentAttributes.DocScopeKey] =
                    [
                        "個人資料（doc_scope=private-note）はこの経路では作成できません。"
                        + "/private-notes（SC-19）または Obsidian 同期から作成してください。"
                    ]
                });

            // SC-05, #635: タグ名を辞書の識別子へ解決する。**辞書に無い名前は 400**（手入力は自動登録しない）。
            var (createTagIds, createUnknown) = await TagResolver.ToIdsAsync(db, req.Tags);
            if (createUnknown.Count > 0) return DocumentEndpoints.UnknownTagsProblem(createUnknown);

            // FR-21 受け入れ基準 ①③④: 本文が付いていればオブジェクトストレージへ格納し、
            // DB へは参照（storage:// URI）だけを持たせる。`OriginalUri` は別列なので**併存する**。
            // ID を先に採るのは、オブジェクトキーが文書 ID から決まるためである。
            // FR-05, FR-21, ADR-0036 D-07, ADR-0060 決定 3 (#1057): **所有者は作成した利用者本人。**
            // 要求由来の `owner` は捨てて主体から入れ直す（`WithOwner` が両方を担う）。
            // 個人資料の作成（`PrivateNoteDefaults`）とコネクタ同期（`DataSourceSyncService`）は
            // 既に `owner` を載せており、**残っていたのはこの一般作成経路だけ**である。
            var createAttributes = DocumentBodyIntake.WithOwner(req.Attributes, http.User.Identity?.Name);

            Document doc;
            if (string.IsNullOrEmpty(req.Body))
            {
                doc = Document.Create(req.Title, req.OriginalUri, req.ContentType,
                    createAttributes, createTagIds);
            }
            else
            {
                var newId = Guid.NewGuid();
                var bodyUri = await storage.PutTextAsync(
                    DocumentBodyIntake.StorageKey(newId), req.Body,
                    DocumentBodyIntake.ContentType, ct);
                doc = Document.CreateWithBody(newId, req.Title, bodyUri,
                    req.OriginalUri, req.ContentType, createAttributes, createTagIds,
                    // ADR-0050 (#911): 本文指紋。イベントが運び、却下解除・再取り込み判定に使う。
                    DocumentBodyIntake.Fingerprint(req.Body));
            }
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            var createNames = await TagResolver.NamesAsync(db);
            await DocumentEndpoints.PublishUpdatedAsync(bus, doc, createNames, ct);
            return Results.Created($"/documents/{doc.Id}",
                DocumentEndpoints.ToDto(doc, createNames));
        });
    }
}
