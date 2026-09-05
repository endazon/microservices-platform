using McpServer.Domain;
using Riok.Mapperly.Abstractions;

namespace McpServer.Features.McpClients;

// FR-16, UC-09, SC-12, 計画 ADR-0030 §決定（マッピング = Riok.Mapperly。選定基準 4「実行時
// リフレクションより コンパイル時生成を優先する」）/ IADR-0371 決定 3 / IADR-0393:
// ドメイン → 応答ビューの写像。
//
// 従前は `McpClientEndpoints.ToView` の手書き詰め替え 1 本であった。9 プロパティのうち
// 7 つは同名の 1:1 で、**2 つだけが値の変換を挟む**（`Kind` の列挙 → 文字列、`EgressTier` の
// 整数 → 文字列）。
//
// 🔴 **変換は `Use =` で明示的に指名する。** 指名せずに `McpClientKind → string` /
// `int → string` の変換メソッドを置くと、Mapperly は**型の組み合わせだけで**それを選ぶ ——
// 将来この写像へ別の `int` の列が入ったとき、**黙ってティア名へ変換される**。
//
// **置き場は 2 段目（`Features/McpClients/`）である。** 一覧・個別・登録・属性差し替えの
// **4 操作が使う**ためであり、`ADR-0068` 決定 2 の適用結果である。**手書きだった頃と変わらない。**
//
// 生成コードは `obj/` 配下に出るため、カバレッジ集計からは既に落ちている（IADR-0195 決定 1）。
// **床は動かない。**
[Mapper]
internal static partial class McpClientMapper
{
    // FR-16, SC-12: 登録済みクライアント → 応答ビュー。実体は source generator が生成する。
    [MapProperty(nameof(McpClient.Kind), nameof(McpClientView.Kind), Use = nameof(KindName))]
    [MapProperty(nameof(McpClient.EgressTier), nameof(McpClientView.EgressTier), Use = nameof(TierName))]
    internal static partial McpClientView ToView(McpClient client);

    // UC-09: 有人／無人の別。**画面と契約の語彙はケバブケースである**（列挙名をそのまま出さない）。
    private static string KindName(McpClientKind kind)
        => kind == McpClientKind.ServiceAccount ? "service-account" : "interactive";

    // ADR-0024 §4, 08_data-egress-policy: データ保護水準ティアの表示名。
    // **既定（未知の値）は最も低い保護水準へ倒す** —— 移送前の `switch` の `_` と同じである。
    private static string TierName(int tier) => (EgressTier)tier switch
    {
        EgressTier.SelfHosted => "self-hosted",
        EgressTier.ProtectedExternal => "protected-external",
        _ => "standard-external"
    };
}
