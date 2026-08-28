using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.Documents;

// FR-06, FR-19, UC-03, UC-11, SC-19, ADR-0057 決定 1, [[IADR-0296]]:
// 完全削除の伝播先のうち**①オブジェクトストレージの本文・資産**を担う。
//
// 🔴 **前方一致の一括削除はできない。** 本文・資産のキー体系は経路ごとに違い
// （`documents/{id:D}/body.md` ／ `{id:N}/document.md` ／ `{id:N}/assets/{figureId}{ext}`）、
// 「この文書のオブジェクト」を 1 つの prefix で表せない。**必ず台帳から逆引きする。**
//
// 台帳＝ ①`Document.MarkdownUri` ②`Document.AssetUris` ③**全 `DocumentVersion.MarkdownUri`**。
// ③が要るのは、本文のキーが経路の切り替え（取り込み → 本文直接受け入れ）で**変わり得る**ためである
// ——現行行だけを見ると、過去に別のキーを指していた本文を取りこぼす。
//
// **`Document.OriginalUri` は対象に含めない。** これは API 要求からしか入らず（取り込み経路は
// 設定しない）、`storage://` を指す場合その実体は DataSourceService が `{sourceId}/{fetchId}/raw{ext}`
// で書いた**別サービス所有の原本**であり得る。本サービスの台帳は原本の参照数を知らないため、
// 消すと DB per Service の境界を越えて他サービスのデータを壊す。
// **この除外が、参照カウントを持たずに済ませる条件である**（他の 3 経路は鍵に文書 ID を含むので、
// 2 つの文書が同じオブジェクトを指すことが構造上起こらない）。
public sealed class DocumentObjectPurger(
    DocumentDbContext db,
    IObjectStorageClient storage,
    ILogger<DocumentObjectPurger> logger)
{
    // 台帳から、当該文書群が指す storage:// オブジェクトの参照 URI を集める（重複は畳む）。
    // 🔴 **DB 行を消す前に呼ぶこと。** 行が消えた後では逆引きの手掛かりが無い。
    public async Task<IReadOnlyList<string>> CollectAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return [];
        var ids = documentIds.ToList();

        var docs = await db.Documents.Where(d => ids.Contains(d.Id))
            .Select(d => new { d.MarkdownUri, d.AssetUris }).ToListAsync(ct);
        var versionUris = await db.DocumentVersions.Where(v => ids.Contains(v.DocumentId))
            .Select(v => v.MarkdownUri).ToListAsync(ct);

        var uris = new List<string?>(versionUris);
        foreach (var d in docs)
        {
            uris.Add(d.MarkdownUri);
            uris.AddRange(d.AssetUris);
        }

        // storage:// 以外（http(s) の外部原本など）は本サービスの持ち物ではない。
        return [.. uris.Where(StorageUri.IsStorageUri).Select(u => u!).Distinct(StringComparer.Ordinal)];
    }

    // 対話操作（FR-06 削除・FR-19 完全削除）向け。**fail-closed**。
    // 1 つでも消せなければ例外がそのまま出て、呼び出し側の `SaveChangesAsync` へ到達しない
    // ＝ **DB 行が残る**。「消したことにして実体を残す」より「消えていないと告げる」ほうを採る
    // （[[IADR-0296]] 決定 3）。
    public async Task PurgeAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        var uris = await CollectAsync(documentIds, ct);
        foreach (var uri in uris) await storage.DeleteAsync(uri, ct);
        if (uris.Count > 0)
            logger.LogInformation("文書 {Count} 件のオブジェクト {Objects} 件を削除した",
                documentIds.Count, uris.Count);
    }

    // 定期処理（90 日自動物理削除）向け。**文書ごとに隔離**し、成功した文書 ID だけを返す。
    // 1 件の失敗で周期全体を止めない —— 止めると無関係な資料の期限超過が積み上がる。
    // 失敗した文書は行を残すので `PurgeAt <= now` を満たしたままであり、**次周期で再試行される**。
    public async Task<IReadOnlyList<Guid>> PurgeIsolatedAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        var purged = new List<Guid>();
        foreach (var id in documentIds)
        {
            try
            {
                await PurgeAsync([id], ct);
                purged.Add(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 行を残すことが「次周期で再試行する」の実体である。黙って消さない。
                logger.LogError(ex,
                    "文書 {Id} のオブジェクト削除に失敗した。台帳の行は残し、次周期で再試行する。", id);
            }
        }
        return purged;
    }
}
