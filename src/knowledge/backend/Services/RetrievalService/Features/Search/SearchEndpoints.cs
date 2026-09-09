using RetrievalService.Features.Search.AttributeValues;
using RetrievalService.Features.Search.Hybrid;

namespace RetrievalService.Features.Search;

// FR-03, FR-04, UC-01: 検索集約の登録表（ADR-0068 決定 1）。
//
// `MapGroup` とタグ付けは集約の全操作が使うものであり、特定の 1 操作に属さない。
// 各操作の処理は `Features/Search/<操作>/` に居る（ADR-0065 決定 2）。
// **`Program.cs` から呼ぶメソッド名とシグネチャは変えない** —— ルート登録順・タグ付け・
// フィルタ適用順が動かないことを、この形で担保する（ADR-0068 決定 1）。
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        // 🔴 FR-05, NFR-09, ADR-0004, ADR-0032, ADR-0084, [[IADR-0044]], [[IADR-0416]] 決定 5,
        // [[IADR-0418]] (#1318 欠陥 B): **この群は認証を要する。**
        //
        // [[IADR-0416]] は「呼び出し元の主張を信じない」形へ変えて**悪用可能性**を塞いだが、
        // **認可そのものは掛けなかった**（SPA / McpServer への影響を測る必要があるとして留保）。
        // 実測の結果、非テストの呼び出し元は 3 つだけで **3 つとも利用者トークンを転送している**
        // （`SearchBffEndpoints` の 2 箇所と `RagOrchestrator`）。McpServer は申告先
        // `/internal/mcp/search_documents` を叩くが、**その路は Map されていない**。
        // ⇒ **契約は 1 バイトも変わらない**。
        //
        // 🔴 **ポリシーは付けない。** `ServiceCaller` を掛けると呼び出し元 3 つが全滅する
        // （運んでいるのは利用者トークンであってサービス資格情報ではない）。realm の
        // 任意の認証済み主体を通し、**見えるものは ABAC が決める**（[[IADR-0044]] の多層防御）。
        // ロール要求も足さない —— 計画 `05_screens` は SC-01 / SC-08 を
        // 「ABAC の権限内で全利用者が利用できる」と定めており、書かれていない制限を足さない。
        //
        // 🔴 **未認証は 401 である。** [[IADR-0416]] 決定 5 の「空応答へ倒す」契約は
        // **認証済みだが権限の無い主体**にだけ残る —— 存在秘匿はそこで保たれる
        // （権限の有無で応答が変わらない。[[IADR-0009]] / [[IADR-0151]] 決定 5）。
        //
        // 🔴 **群の外は触らない**（`/health/*`・`/internal/mcp-tools`・introspection・OpenAPI）。
        // gRPC 面は `ServiceCaller` のままである（[[IADR-0417]] 決定 4）——
        // REST が利用者トークンで通る非対称は**並走の期間だけ**続く。
        var g = app.MapGroup("/search").WithTags("Search").RequireAuthorization();

        SearchEndpoint.Map(g);
        AttributeValuesEndpoint.Map(g);

        return app;
    }
}
