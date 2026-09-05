using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.Documents.PutBody;

// FR-21, UC-03: 文書本文の直接受け入れ（既存文書への投入）。
//
// 🔴 **本経路にロールは積まれていない。** FR-21 要求文は「本文の書き込み権限は ABAC の**動的束縛**
// （`doc.owner ∈ { ${current_user} }`）で表現し、**ロールによる判定を追加しない**」と定めており、
// ADR-0036 D-07 も同じことを述べている。合成点で `bodyIntake`（認証のみ）の群に載せているのは
// そのためである —— **`write` 群（admin / operator）へ移してはならない。**
internal static class PutDocumentBodyEndpoint
{
    internal static void Map(RouteGroupBuilder bodyIntake)
    {
        bodyIntake.MapPut("/{id:guid}/body", async (Guid id, UpdateDocumentBodyRequest req,
            IValidator<UpdateDocumentBodyRequest> validator,
            DocumentDbContext db, IObjectStorageClient storage, IDocumentUpdatedPublisher bus,
            HttpContext http, CancellationToken ct) =>
        {
            // FR-21 / 計画 ADR-0030 §決定 / IADR-0371 決定 2 / [[IADR-0398]] 決定 1:
            // 本文は必須（`null` は不可。**空文字は有効**）。規則は `PutDocumentBodyValidator` が持つ。
            //
            // 🔴 **この位置（取得・認可・413 より前）を動かしてはならない** —— 移送前もそうだった。
            // 1 MB 超（413）は認可の後ろに居る別の判定であり、検証器には入っていない。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

            // 🔴 検証器が `null` を弾いた後なので非 null である（`EdgeTypes/Create` の `req.Layer!` と
            // 同じ形。IADR-0395 の移送で確立した作法）。**ここで `is null` を書き直さない** ——
            // 同じ不変条件が 2 箇所になり、どちらかだけが直る。
            var body = req.Body!;

            var doc = await db.Documents.FindAsync([id], ct);
            if (doc is null) return Results.NotFound();

            // FR-21 受け入れ基準 ⑤⑧, ADR-0036 D-02/D-07/D-14: 所有者ベースの動的束縛で判定する。
            // **主体が判定の入力である** —— 同じ文書 ID でも別の利用者なら拒否される（⑧）。
            // 認可を先に見るのは、他人の文書に対する副作用（格納）をサイズ判定より先に止めるためである。
            //
            // 🔴 **拒否は 404 である。403 にしない**（ADR-0056 決定 1・[[IADR-0277]]）。
            // 打ち分けの軸は「主体がその文書を読めるか」であり、**本サービスは ABAC の
            // 読み取り判定を持たない**ため「読めるが書けない」（403 が許される決定 2 の側）だと
            // 言い切れない。403 を返すと**文書 ID の総当たりで実在が判別できてしまう**。
            if (!DocumentBodyIntake.CanWrite(doc.Attributes, http.User.Identity?.Name))
                return Results.NotFound();

            // FR-21 受け入れ基準 ⑥: 1 MB 超は 413。**切り詰めない。**
            if (DocumentBodyIntake.ExceedsLimit(body))
                return DocumentEndpoints.BodyTooLargeProblem();

            // FR-21 受け入れ基準 ④⑦: 全文をオブジェクトストレージへ格納し、DB は参照のみ持つ。
            var bodyUri = await storage.PutTextAsync(
                DocumentBodyIntake.StorageKey(doc.Id), body,
                DocumentBodyIntake.ContentType, ct);
            // ADR-0050 (#911): 本文指紋。イベントが運び、却下解除・再取り込み判定に使う。
            doc.SetMarkdownUri(bodyUri, DocumentBodyIntake.Fingerprint(body));
            await db.SaveChangesAsync(ct);

            // FR-21 受け入れ基準 ①②: DocumentUpdated が取り込み（parse→chunk→embed→index）を起動し、
            // 索引反映を経て RAG 検索の結果として返るようになる。
            var bodyNames = await TagResolver.NamesAsync(db);
            await DocumentEndpoints.PublishUpdatedAsync(bus, db, doc, bodyNames, ct);
            return Results.Ok(DocumentEndpoints.ToDto(doc, bodyNames));
        }).WithName("DocumentBodyPut");
    }
}
