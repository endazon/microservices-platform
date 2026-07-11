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
    // 順序は既存 Program.cs の登録順を維持する。
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
        new DelegateBffEndpointModule(a => a.MapDataSourceBffEndpoints()),
    ];

    // 合成点の全モジュールを Map する（Program.cs はこの 1 行を呼ぶ）。
    public static IEndpointRouteBuilder MapComposedBffEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var module in Modules)
            module.Map(app);
        return app;
    }
}
