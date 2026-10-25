using Platform.Shared.Contracts.Dtos;

namespace Knowledge.Contracts.Dtos;

// FR-03, UC-01: ハイブリッド検索リクエスト/レスポンス DTO
public record SearchRequest(
    string Query,
    int TopK = 10,
    // FR-03: 単値完全一致フィルタ（後方互換）。key → 単一の許可値。
    Dictionary<string, string>? AttributeFilters = null,
    // FR-05: ABAC アクセススコープ（多値 allow-list ＋ deny-by-default）。
    AccessScope? Scope = null,
    // FR-03, SC-02, #531: 検索モード。**3 値**（hybrid〔既定〕/ keyword / semantic）。
    // 既定値を持たせて追加する（既定値の無いメンバー追加は契約上の破壊的変更）。
    // 未知の値・null は既定（hybrid）へ縮退する＝旧クライアントは従来どおり動く。
    string? Mode = null,
    // FR-03, SC-02, #532: 並び順。**2 値**（relevance〔既定〕/ updated）。利用者裁定 Q5 / planning#197。
    // 既定値を持たせて追加する（既定値の無いメンバー追加は契約上の破壊的変更）。
    // 未知の値・null は既定（relevance）へ縮退する。
    string? SortBy = null);

// FR-03, SC-02, #531: 検索モードの値集合。
// **2 値（キーワード｜意味）にしてはならない。** 現行は常時ハイブリッドで動いており、
// 2 値にすると利用者がハイブリッドを選べなくなり機能後退になる（利用者裁定 Q4 / planning#197）。
// enum ではなく文字列 + const で持つ（IADR-0131 決定 5 と同じ理由。後段の値追加を
// SPA 側の破壊的変更にしない）。
public static class SearchModes
{
    public const string Hybrid = "hybrid";
    public const string Keyword = "keyword";
    public const string Semantic = "semantic";

    public static readonly string[] All = [Hybrid, Keyword, Semantic];

    public static bool IsValid(string? mode) =>
        mode is not null && All.Contains(mode, StringComparer.OrdinalIgnoreCase);

    // 未知・未指定は既定（hybrid）へ縮退する。呼び出し側の分岐を 1 か所に閉じるための正規化。
    //
    // 🔴 CodeQL(cs/log-forging) アラート #24 (#1019): **許可リスト側の定数を返す。**
    // 従前は `mode!.ToLowerInvariant()` を返していた —— 値は定数と一致するが、返る**実体は
    // 利用者入力から作った新しい文字列**であり、テイントが下流（`HybridSearchService` の
    // ログ）まで伝播していた。CodeQL は正しく追っていた。
    //
    // 発生源で断つのが正しい直し方である（sink 側の sanitize ではない）。同型の先例は
    // `LlmRouter.ResolveModel`（「利用者由来の文字列ではなく設定側が保持する正規の文字列を
    // 返す。テイント源を選択結果に持ち込まない」）。
    //
    // **観測可能な振る舞いは変わらない。** `IsValid` は OrdinalIgnoreCase の一致なので、
    // 妥当な入力の `ToLowerInvariant()` は必ず当該定数と文字列等価である。変わるのは実体だけ。
    public static string Normalize(string? mode) =>
        All.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase))
        ?? Hybrid;
}

// FR-03, SC-02, #532: 並び順の値集合。**2 値に確定している**（利用者裁定 Q5 / planning#197）——
// タイトル順・作成者順は採らない（「全文検索の結果を五十音で並べる場面が実務でほぼ無く、
// 選択肢が増えると利用者が迷う」。必要になった時点で足す）。
// enum ではなく文字列 + const で持つ（IADR-0131 決定 5・SearchModes と同じ理由）。
//
// **`updated` は取得後に並べ替える**（IADR-0150 決定 1）。関連度は候補の門番として残り、
// 並び順は表示順だけを決める —— Qdrant の order_by はスコアリングを置き換えるため、
// 使うと検索語が順位に一切効かなくなる。
public static class SearchSorts
{
    public const string Relevance = "relevance";
    public const string Updated = "updated";

    public static readonly string[] All = [Relevance, Updated];

    public static bool IsValid(string? sort) =>
        sort is not null && All.Contains(sort, StringComparer.OrdinalIgnoreCase);

    // 未知・未指定は既定（relevance）へ縮退する。呼び出し側の分岐を 1 か所に閉じるための正規化。
    //
    // **`SearchModes.Normalize` と同一構造なので同じ形にする**（#1019）。現時点で `sort` は
    // ログへ出ていないが、片方だけ直すと次に読む人が「なぜ片方だけ」を復元できない。
    public static string Normalize(string? sort) =>
        All.FirstOrDefault(s => string.Equals(s, sort, StringComparison.OrdinalIgnoreCase))
        ?? Relevance;
}

// FR-03, NFR-06, ADR-0016, [[IADR-0534]] (#1871): 縮退の印は**位置引数ではなく init で足す**（既存の生成箇所を壊さない）。
// 早期の空応答（スコープ無し・deny・空クエリ）は既定の「縮退なし」のまま返る —— 部品を 1 つも呼んでいないからである。
public record SearchResponse(
    List<SearchResultDto> Results,
    int TotalHits,
    long ElapsedMs)
{
    // 部品のどれかが働かず、結果がその分だけ劣化して返ったか。`DegradedReasons` が空でないことと常に一致させる
    // （設定は `WithDegradation` の 1 か所だけで行う）。
    public bool Degraded { get; init; }

    // 縮退の理由。🔴 **`SearchDegradedReasons.All` の固定語彙だけ**（本文・URL・資格情報・例外メッセージを載せない）。
    // 並びは `All` の順で、重複しない。
    public List<string> DegradedReasons { get; init; } = [];

    // 縮退の 2 項目を揃えて設定する唯一の口（`Degraded` と `DegradedReasons` を食い違わせない）。
    public SearchResponse WithDegradation(IReadOnlyCollection<string> reasons) =>
        this with { Degraded = reasons.Count > 0, DegradedReasons = [.. reasons] };
}

// FR-03, NFR-06, ADR-0016, ADR-0018, ADR-0035, ADR-0127, [[IADR-0534]] (#1871): **検索の縮退の理由（固定語彙）。**
//
// 🔴 **部品の健全性だけを表す。** 権限が無いのか該当が無いのか（[[IADR-0009]] / [[IADR-0313]] 決定 1）は表さない ——
// ABAC の deny は埋め込みより前に空で返り、ここに来ない。
// 🔴 **構成で無効な段・設計どおり掛けない段は縮退ではない**（ADR-0018 の既定オフ。数えると既定構成の全検索が縮退になる）。
// gRPC は同じ集合を enum `SearchDegradedReason` で運ぶ（`document_search.proto`）。足すときは両方へ足す。
public static class SearchDegradedReasons
{
    // 主コレクションのクエリ埋め込みが空ベクトルで返った（送信拒否・鍵なし・次元不整合・ゲートウェイ側の失敗）。
    // hybrid は語彙検索だけで返り、semantic は 0 件で返る。
    public const string EmbedFailed = "embed-failed";

    // ベクトルの系統を持つ追加コレクションのどれかで、クエリ埋め込みが空ベクトルで返った（語彙索引は数えない）。
    public const string FusedEmbedFailed = "fused-embed-failed";

    // 二段検索の段が登録されていて、近傍・辺の型の辞書が引けなかった（または利用者文脈が無く呼べなかった）。
    public const string GraphExpandFailed = "graph-expand-failed";

    // 再順位付けの段が登録されていて、掛けようとして元の順へ戻した。
    public const string RerankFailed = "rerank-failed";

    // 並びの正（応答の `DegradedReasons` はこの順に並ぶ）。
    public static readonly IReadOnlyList<string> All = [EmbedFailed, FusedEmbedFailed, GraphExpandFailed, RerankFailed];

    // 理由を正の順に並べ、重複を除く。
    public static List<string> Normalize(IEnumerable<string> reasons)
    {
        var set = reasons.ToHashSet(StringComparer.Ordinal);
        return All.Where(set.Contains).ToList();
    }
}
