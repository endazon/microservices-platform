using Microsoft.EntityFrameworkCore;
using DocumentService.Domain.Ports;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.Archive;

// FR-06, UC-03, Issue #88: 文書をアーカイブ（非公開化）する。下流の Wiki.js 同期が
// status=archived を受けてページを非公開化・メタデータ Archived 化する。
// FR-06, UC-03, SC-05（#629）: アーカイブは**管理者限定**。公開と同じ基準で分類した
// （作業仕様書 §判断 1）。**アーカイブは (a) すら満たさない** —— 下流の Wiki.js 同期が
// ページを非公開化するため、**可視性を落とす**。「既存データを壊さない」とは言い切れない。
internal static class ArchiveDocumentEndpoint
{
    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost("/{id:guid}/archive", async (Guid id, DocumentDbContext db,
            IDocumentUpdatedPublisher bus, CancellationToken ct) =>
        {
            // FR-19, ADR-0036 D-08, ADR-0119 決定 3 (#1629): 個人資料はこの口の対象外（主体を問わず 404）。
            var doc = await DocumentManageScope.FindManageableAsync(db, id, ct);
            if (doc is null) return Results.NotFound();
            doc.Archive();
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
