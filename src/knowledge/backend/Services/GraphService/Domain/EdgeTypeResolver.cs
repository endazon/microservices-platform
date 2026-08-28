namespace GraphService.Domain;

// FR-17, ADR-0033 決定 3・8, IADR-0281 (#912): 抽出したリンクを**辺の型の名前**へ解決する純粋関数。
//
// ## 3 層の既定（ADR-0033 決定 8 の写像）
//
//   ① 明示型   フロントマターのキー名（`supersedes:` 等）。**書き手の明示が最も強い。**
//   ② 文脈既定 `[[note#見出し]]` → `cites` / `![[note]]` → `embeds`
//   ③ 既定     `[[note]]` / `[text](uri)` → `related`
//
// **辞書は実行時に変わる**（ADR-0033 決定 3。値の追加・改名は SC-09 から行い、コードは触らない）。
// したがって本関数は「その名前が今の辞書にあるか」を引数で受け取り、**型 ID も EdgeType も知らない**。
//
// ## 未定義型（決定 3）
//
// **拒否も破棄もしない。`related` へ丸め、フォールバックとして印を返す** —— 拒否すると取り込み
// 全体が落ち、破棄すると辺そのものが失われる。呼び出し側は印を見て警告とカウンタを記録する。
public static class EdgeTypeResolver
{
    // ADR-0033 決定 3: 自動抽出の既定型。未定義型のフォールバック先でもある。
    // **EdgeTypeSeed.DefaultTypeName と同じ値である**（seed は初期データ、こちらは解決規則。
    // Domain は Api を参照できないため定数は共有できない。値が割れたら EdgeTypeResolverTests が落ちる）。
    public const string DefaultTypeName = "related";

    // 文脈既定の写像先（ADR-0033 決定 8 の表）。
    public const string SectionTypeName = "cites";
    public const string EmbedTypeName = "embeds";

    // 解決結果。`IsFallback` が true のとき `RequestedTypeName` に**辞書に無かった名前**が入る
    // （ログとカウンタのため。型名は基数が無界なのでメトリクスのタグにはしない）。
    public readonly record struct EdgeTypeResolution(
        string TypeName, bool IsFallback, string? RequestedTypeName);

    // 解決できないとき（＝既定型 `related` すら辞書に無いとき）は null を返す。
    // **辺を作らない側に倒す** —— 存在しない型 ID の辺は後から作れない
    // （AiSuggestionGenerator.PersistAsync の「seed 前で辞書が空なら作らない」と同じ倒し方）。
    public static EdgeTypeResolution? Resolve(ObsidianLink link, IReadOnlySet<string> knownTypeNames)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(knownTypeNames);

        // ① 明示型。
        if (link.ExplicitTypeName is { Length: > 0 } explicitName)
            return Prefer(explicitName, knownTypeNames);

        // ② 文脈既定。
        var contextual = link.Kind switch
        {
            ObsidianLinkKind.SectionReference => SectionTypeName,
            ObsidianLinkKind.Embed => EmbedTypeName,
            _ => null,
        };
        if (contextual is not null)
            return Prefer(contextual, knownTypeNames);

        // ③ 既定。
        return Fallback(knownTypeNames, requested: null);
    }

    // 望ましい型名が辞書にあればそれを、無ければ既定型へ丸めてフォールバックの印を付ける。
    private static EdgeTypeResolution? Prefer(string typeName, IReadOnlySet<string> known)
        => known.Contains(typeName)
            ? new EdgeTypeResolution(typeName, IsFallback: false, RequestedTypeName: null)
            : Fallback(known, typeName);

    private static EdgeTypeResolution? Fallback(IReadOnlySet<string> known, string? requested)
        => known.Contains(DefaultTypeName)
            ? new EdgeTypeResolution(DefaultTypeName, requested is not null, requested)
            : null;
}
