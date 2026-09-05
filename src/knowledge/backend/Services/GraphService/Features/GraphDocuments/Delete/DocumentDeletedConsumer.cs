using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Pipeline;

namespace GraphService.Features.GraphDocuments.Delete;

// FR-17, FR-06, FR-19, UC-03, ADR-0057 (#1016): 文書削除イベントを受信し、グラフから
// 当該文書の痕跡（ノード・両端いずれかが当該文書の辺・AI 提案）を掃除する。
//
// - **ノード**: `graph_documents` の複製行。残すと ABAC 属性の複製が孤児として残り続ける。
// - **辺**: 両端いずれかが当該文書のもの。provenance を問わない（利用者付与・AI 承認済みも、
//   端点の文書が消えた辺は指す先が無い）。ADR-0033 決定 6 の「利用者付与は再取り込みで消さない」は
//   **再取り込み（差分更新）**の話であり、文書そのものの削除には適用されない。
// - **リンク先の名前**: 当該文書が**書いていた**もの（`document_link_targets`）。残すと消えた文書の
//   リンクが未解決リンク数に永久に積み上がる。**逆向き（他文書が当該文書を指すリンク）は消さない** ——
//   あちらの本文はいま壊れたのであり、未解決として数えられるのが正しい（[[IADR-0389]] / #1246）。
// - **AI 提案**: 当該文書を起点・対象とするもの（pending・rejected を含む全状態）。
//   却下レコードの「原則永久保持」（ADR-0033 決定 10）は再提案抑止のためであり、
//   端点の文書が消えれば同じ組み合わせの提案は二度と生成されない（候補に現れない）。
//
// 冪等性: すべて文書 ID による削除で、該当 0 件でも成功する（再配信に対して冪等）。
// 失敗時: 例外を送出し、Wolverine のリトライ／デッドレター（UsePlatformMessagingDefaults）へ委ねる。
//
// 🔴 EF の一括削除 API（ExecuteDelete）は使わない —— テスト器（InMemory プロバイダ）が
// サポートせず、削除件数も高々文書 1 件ぶんの近傍なので取得してから消す。
public class DocumentDeletedConsumer(
    GraphDbContext db,
    ILogger<DocumentDeletedConsumer> logger) : IPipelineStep<DocumentDeleted>
{
    // FR-14, ADR-0018: 宣言的パイプライン構成上の段名（pipeline.json steps[].name）。
    public static string StepName => "graph-delete";

    // ADR-0027 / #1016: Wolverine のハンドラ。
    public async Task Handle(DocumentDeleted ev, CancellationToken ct)
    {
        var id = ev.DocumentId;

        var edges = await db.Edges
            .Where(e => e.SourceDocumentId == id || e.TargetDocumentId == id)
            .ToListAsync(ct);
        db.Edges.RemoveRange(edges);

        var suggestions = await db.AiSuggestions
            .Where(s => s.SourceDocumentId == id || s.TargetDocumentId == id)
            .ToListAsync(ct);
        db.AiSuggestions.RemoveRange(suggestions);

        // FR-10, SC-10, [[IADR-0389]] (#1246): 当該文書が**書いていた**リンク先の名前。
        // 🔴 残すと、消えた文書のリンクが未解決リンク数に**永久に**積み上がる。
        //
        // ⚠️ 逆向き（**他文書が当該文書を指していたリンク**）はここでは触らない ——
        // あちらの本文はいま壊れたのであり、**未解決として数えられるのが正しい。**
        // だから解決の失敗ではなく名前を保存している（同決定 3）。
        var linkTargets = await db.DocumentLinkTargets
            .Where(t => t.SourceDocumentId == id)
            .ToListAsync(ct);
        db.DocumentLinkTargets.RemoveRange(linkTargets);

        var node = await db.Documents.FirstOrDefaultAsync(d => d.DocumentId == id, ct);
        if (node is not null)
            db.Documents.Remove(node);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Removed deleted document {DocumentId} from the graph: node={Node} edges={Edges} "
            + "suggestions={Suggestions} linkTargets={LinkTargets}",
            id, node is not null ? 1 : 0, edges.Count, suggestions.Count, linkTargets.Count);
    }
}
