using DocumentService.Features.SyncHistory.List;

namespace DocumentService.Features.SyncHistory;

// FR-20, UC-11, SC-20 主要素 6, ADR-0037 決定 9, ADR-0099, planning#618, #1446:
// 同期履歴の合成点（現在は一覧の 1 操作だけ）。
//
// ADR-0065 決定 2: 操作の実体は `Features/SyncHistory/<操作>/` に居る。
//
// **群は `/private-notes` である**（`/private-notes/sync-history` は個人資料の従属資源であり、
// 認可の姿も同じ＝認証必須・ロール不問）。同期設定・競合のように専用の接頭辞を切らないのは、
// **口が 1 つで従属関係も 1 段しかない**ためである（`/private-notes/sync-history/{...}` は無い）。
//
// 🔴 **書き込みの口を作らない。** 監査ログは追記だけで、利用者が消す・直す手段を持たない
// （消せるなら監査ログではない）。削除は保持期限（3 年）の定期処理だけが行う。
public static class SyncHistoryEndpoints
{
    public static IEndpointRouteBuilder MapSyncHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/private-notes").WithTags("PrivateNotes").RequireAuthorization();

        ListSyncHistoryEndpoint.Map(g);

        return app;
    }
}
