using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.Publish;

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
internal static class PublishDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost("/{id:guid}/publish", async (Guid id, DocumentDbContext db,
            IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            // FR-19, ADR-0036 D-08, ADR-0119 決定 3 (#1629): 個人資料はこの口の対象外（主体を問わず 404）。
            var doc = await DocumentManageScope.FindManageableAsync(db, id, ct);
            if (doc is null) return Results.NotFound();
            if (!doc.CanPublish)
                return Results.Conflict(new
                {
                    error = "invalid_transition",
                    from = doc.Status,
                    to = DocumentStatus.Published
                });
            doc.Publish();
            // FR-06, ADR-0027 §決定, IADR-0608 (#2168): 保存と発行を 1 トランザクションで確定する（Transactional Outbox）。
            // FR-02, [[IADR-0597]] / IADR-0637 決定 7 (#2150 段 1c): 確定は並行トークン（版・発行の連番）で照合される。読んだ後に同じ文書へ
            // 別の確定（Touch しない状態の変化を含む）が入れば確定ごと負ける。500 にせず #2103 と同じ 409 `version_conflict` を返す。
            IReadOnlyDictionary<Guid, string> names;
            try
            {
                names = await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(bus, db, doc, ct);
            }
            catch (DbUpdateException ex) when (DocumentVersionConflict.Is(ex))
            {
                return await DocumentVersionConflict.AfterLostSaveAsync(db, id, expectedVersion: null, ct);
            }
            return Results.Ok(await DocumentEndpoints.ToDtoAsync(db, doc, names, ct));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }
}
