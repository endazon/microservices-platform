using Platform.Shared.Contracts.Dtos;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, FR-05, UC-09, SC-12, ADR-0036 D-01・D-02, ADR-0062 決定 2・3, [[IADR-0384]] 決定 1 (#1242),
// [[IADR-0401]] 決定 4 (#1255): 認可スコープから「登録者が渡してよい機密区分」を読む規則。
//
// 🔴 **読み方は 1 か所だけである**（[[IADR-0384]] 決定 1）。#1255 で輸送が 2 つ（REST と gRPC）に
// なったため、両実装が呼べる位置へ**一字も変えずに**括り出した。
// **写しを 2 つ作らない** —— 片方だけが fail-open へ戻る事故（#1242）が輸送ごとに再現し得る。
internal static class RegistrarScopeReading
{
    // 文書側の機密区分キー。`clearance`（主体側）と同一の値域を持つ（07_abac-attribute-model）。
    private const string ConfidentialityKey = "confidentiality";

    /// <summary>
    /// 認可スコープから「登録者が渡してよい機密区分」を読む。**#1242 / IADR-0384 の規則の実体。**
    ///
    /// 🔴 **不在を「制約なし」と読まない。** 規則は次の 3 段である。
    ///
    /// <list type="number">
    ///   <item><c>Granted == false</c> → 空集合（読めるものが無い＝配れるものも無い）。</item>
    ///   <item>
    ///     <c>Branches</c> が 1 件以上 → **分岐ごとに見る**（分岐＝マッチしたポリシー 1 本の連言）。
    ///     <list type="bullet">
    ///       <item>フィルタを 1 つも持たない分岐 → **無制限**。計画 07_abac-attribute-model
    ///       §ポリシー評価モデル「マッチしたポリシーに文書条件が無い場合は全件許可する」。</item>
    ///       <item>フィルタがちょうど 1 つで、そのキーが <c>confidentiality</c> → その許可値を足す。</item>
    ///       <item>🔴 **それ以外の分岐は何も足さない。** <c>owner</c> だけの分岐はもちろん、
    ///       <c>{owner, confidentiality}</c> のような連言も数えない —— それは「**自分が持つ**
    ///       restricted 文書を読める」であって「restricted を読める」ではなく、
    ///       **サービスアカウントは登録者の所有権も部門も継がない**。</item>
    ///     </list>
    ///   </item>
    ///   <item>
    ///     <c>Branches</c> が空／null（未移行の発行者。契約の後方互換規則） →
    ///     <c>AllowedFilters</c> が**空**なら無制限（契約 <c>AccessScopeResponse</c> の明文）。
    ///     キーが <c>confidentiality</c> **ただ 1 つ**ならその許可値。それ以外は空集合。
    ///   </item>
    /// </list>
    ///
    /// **過小に倒れうることは受容する。** 07_abac-attribute-model は「消費側が選言へ対応するまで
    /// **多キーの文書条件を持つポリシーを運用しない**」を暫定の統制として定めており、
    /// 多キーの分岐は運用上そもそも存在しない。現 seed の階段ポリシーは 1 件も落ちない。
    /// </summary>
    internal static (bool Unrestricted, IReadOnlyList<string> Confidentiality) ReadAssignableConfidentiality(
        AccessScopeResponse scope)
    {
        // 許可ポリシーが 1 つも無い＝読めるものが無い。**配れるものも無い**（引けなかったのではない）。
        if (!scope.Granted) return (false, []);

        if (scope.Branches is { Count: > 0 } branches)
        {
            var values = new List<string>();
            foreach (var branch in branches)
            {
                var filters = branch.Filters ?? [];
                // 文書条件を持たない分岐＝そのポリシーの範囲で全件許可（計画の具体判定規則）。
                if (filters.Count == 0) return (true, []);
                if (filters.Count == 1 && IsConfidentiality(filters[0].Key))
                    values.AddRange(filters[0].AllowedValues);
            }
            return (false, Distinct(values));
        }

        // 後方互換（Branches を運ばない発行者）。**「空である」ことを積極的に確かめる** ——
        // 不在から無制限を推論しない。
        if (scope.AllowedFilters.Count == 0) return (true, []);
        return scope.AllowedFilters is [{ } only] && IsConfidentiality(only.Key)
            ? (false, only.AllowedValues)
            : (false, []);
    }

    private static bool IsConfidentiality(string key)
        => string.Equals(key, ConfidentialityKey, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values)
        => [.. values.Distinct(StringComparer.OrdinalIgnoreCase)];
}
