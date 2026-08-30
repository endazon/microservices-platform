using DashboardService.Features.KnowledgeHealth.Report;
using DashboardService.Features.KnowledgeHealth.View;

namespace DashboardService.Features.KnowledgeHealth;

// FR-10, FR-17, FR-18, UC-05, SC-10, ADR-0006 (#443): ナレッジ健全性の指標集約の登録表
// （ADR-0068 決定 1）。
//
// 計画 `06_technical/05_observability-ops.md` §ナレッジ健全性の指標（集計範囲・2026-08-02 確定）が
// **同時に満たすべき 4 つの規則**を定めている。**本節は ABAC の文書単位判定に対する明示的な例外**であり、
// 🔴 **3 条件（件数のみ・ロール限定・個人資料除外）のうち 1 つでも欠けると存在秘匿が崩れる。個別に緩めない。**
//
//  1. **集計範囲は全体**（閲覧者の権限で絞らない）。運用者ごとに数字が変わると、指標が改善したのか
//     担当者が変わっただけなのかを判別できず、時系列の比較が成り立たない。
//  2. **閲覧は運用者・システム管理者に限定**。全体集計を許す以上、**閲覧側のロール制限が唯一の統制点**である。
//  3. **個人資料（`private-note`）は集計から除外**。所有者本人が閲覧する場合も含め**一律**である
//     （例外を設けると集計値がロールごとに変わり、1 の前提が崩れる）。除外は
//     **件数の変動から個人資料の存在・増減が推測される経路を塞ぐ**意味も持つ（ADR-0034 決定 2 と同じ理由）。
//  4. **個々の文書名を出さず件数のみ**。ドリルダウンの導線を設けない。
//
// **画面（SC-10 のナレッジ健全性節）は本 issue の射程外**である（引き受け先は #452 / #504）。
// ここで用意するのは集計と統制であり、表示ではない。
//
// ADR-0065 決定 2 / ADR-0068 決定 1: 各操作の処理は `Features/KnowledgeHealth/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— 閲覧側の route group と、
// 受け口を `app` 直下へ登録する順序。**受け口は `/internal/...` にあり group の外である。**
public static class KnowledgeHealthEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/dashboard/knowledge-health").WithTags("KnowledgeHealth");

        KnowledgeHealthViewEndpoint.Map(g);
        ReportKnowledgeHealthEndpoint.Map(app);

        return app;
    }
}
