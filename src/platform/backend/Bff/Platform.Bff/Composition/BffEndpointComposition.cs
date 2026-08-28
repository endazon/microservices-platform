using AiStockTrading.Bff.Endpoints;
using Knowledge.Bff.Endpoints;
using Platform.Bff.Foundation.Endpoints;

namespace Platform.Bff.Composition;

// FR-14, IADR-0063, Issue #229: BFF のユニット別エンドポイント合成点（器）。
// 有効な BFF エンドポイントモジュールをここで束ねる唯一の合成点（フロントの features/index.ts の BFF 版）。
// 追加可変機能ユニットは、自ユニットの BFF エンドポイント（IBffEndpointModule 実装）を本合成点へ 1 行追加して
// 組み込む（依存規則の例外3。実装は後続スライスで knowledge へ移設＋合成点参照へ移行する）。
// 本スライス（器）では既存の 9 モジュールを合成点経由の列挙登録へ置換する（挙動不変）。

// BFF エンドポイントモジュールの契約。各モジュールは自分のルート群を app へ Map する。
public interface IBffEndpointModule
{
    IEndpointRouteBuilder Map(IEndpointRouteBuilder app);
}

// 既存の静的 Map*BffEndpoints 拡張メソッドを IBffEndpointModule へ束ねるアダプタ。
internal sealed class DelegateBffEndpointModule(Func<IEndpointRouteBuilder, IEndpointRouteBuilder> map) : IBffEndpointModule
{
    public IEndpointRouteBuilder Map(IEndpointRouteBuilder app) => map(app);
}

public static class BffEndpointComposition
{
    // 合成点（登録簿）: 有効な BFF エンドポイントモジュール。ユニット追加時はここへ 1 行追加する。
    // platform 固有（Config/Authz）は platform 同居、ナレッジ 7 ドメイン（Search/Document/Analysis/Feedback/
    // Dashboard/Conversion/DataSource）は Knowledge.Bff.Endpoints へ移設済みで例外3 により参照する（#229 完了）。
    // AST 3 モジュール（Assumptions/RiskControls/Monitor）も AiStockTrading.Bff.Endpoints（submodule）へ移設済みで
    // 例外3 により参照する（#286 完了・IADR-0073）。順序は既存 Program.cs の登録順を維持する。
    public static IReadOnlyList<IBffEndpointModule> Modules { get; } =
    [
        new DelegateBffEndpointModule(a => a.MapSearchBffEndpoints()),
        new DelegateBffEndpointModule(a => a.MapDocumentBffEndpoints()),
        new DelegateBffEndpointModule(a => a.MapAnalysisBffEndpoints()),
        new DelegateBffEndpointModule(a => a.MapFeedbackBffEndpoints()),
        new DelegateBffEndpointModule(a => a.MapDashboardBffEndpoints()),
        new DelegateBffEndpointModule(a => a.MapConfigBffEndpoints()),
        new DelegateBffEndpointModule(a => a.MapConversionBffEndpoints()),
        new DelegateBffEndpointModule(a => a.MapAuthzBffEndpoints()),
        // NFR, SC-16, ADR-0032 / IADR-0251 / #439 第 3 段(3a): BFF セッションの入口
        // （ログイン開始・ログアウト・現在の身元）。コールバックは OIDC ハンドラが
        // `/bff/auth/callback` で受けるため、ここには現れない。
        new DelegateBffEndpointModule(a => a.MapAuthBffEndpoints()),
        new DelegateBffEndpointModule(a => a.MapDataSourceBffEndpoints()),
        // Issue #640, FR-09, SC-09, IADR-0152/0153: タグ辞書の管理（追加・改名・削除）。
        // 後段は DocumentService（knowledge ユニット）なので Knowledge.Bff.Endpoints に置く
        // （platform 側の /bff/admin/authz へ寄せない。作業仕様書 §判断 1）。
        new DelegateBffEndpointModule(a => a.MapTagDictionaryBffEndpoints()),
        // Issue #916a, FR-17, UC-10, ADR-0034: グラフ読み取りの公開（GraphService へ pass-through）。
        // **Authorization を伝播する方式**を採る（後段が自分で ABAC を解決する型のため）。
        new DelegateBffEndpointModule(a => a.MapGraphBffEndpoints()),
        // Issue #451, FR-19, FR-20, UC-11, SC-19, SC-20, ADR-0036/0037/0054: 個人資料と
        // Obsidian 連携設定（DocumentService の /private-notes* へ pass-through）。
        // 後段は knowledge ユニットなので Knowledge.Bff.Endpoints に置く（タグ辞書と同じ切り分け）。
        // **本人性は後段の台帳が判定する**ので、BFF は認証必須＋資格情報の転送を担う。
        new DelegateBffEndpointModule(a => a.MapPrivateNoteBffEndpoints()),
        // Issue #452, FR-16, UC-09, SC-12, ADR-0024: MCP クライアント登録管理（McpServer の
        // /mcp-clients* へ pass-through）。後段は platform ユニットなので platform 同居とする。
        // **管理者限定**（05_screens §共通シェル「SC-09・SC-12・SC-17 = システム管理者」）。
        new DelegateBffEndpointModule(a => a.MapMcpClientBffEndpoints()),
        // Issue #283/#286, AST/FR-17, AST/UC-06, IADR-0070/0073: AST 設定画面（全体前提条件）の BFF 集約
        // （ConfigurationService へ pass-through）。AiStockTrading.Bff.Endpoints（AST unit-owned Bff・例外3）を参照。
        new DelegateBffEndpointModule(a => a.MapAssumptionsBffEndpoints()),
        // Issue #287/#286, FR-14, IADR-0071/0073: AST リスク設定（AST/SC-02）・統制状態参照（AST/SC-03）の BFF 集約
        // （RiskManagementService /risk-controls/* へ pass-through）。AiStockTrading.Bff.Endpoints（例外3）を参照。
        new DelegateBffEndpointModule(a => a.MapRiskControlsBffEndpoints()),
        // Issue #288/#286, FR-14, IADR-0072/0073: AST 監視銘柄（AST/SC-02 watchlist）の BFF 集約
        // （MarketMonitorService /monitor/* へ pass-through）。AiStockTrading.Bff.Endpoints（例外3）を参照。
        new DelegateBffEndpointModule(a => a.MapMonitorBffEndpoints()),
    ];

    // 合成点の全モジュールを Map する（Program.cs はこの 1 行を呼ぶ）。
    public static IEndpointRouteBuilder MapComposedBffEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var module in Modules)
            module.Map(app);
        return app;
    }
}
