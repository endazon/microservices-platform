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
    public static string Normalize(string? mode) =>
        IsValid(mode) ? mode!.ToLowerInvariant() : Hybrid;
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
    public static string Normalize(string? sort) =>
        IsValid(sort) ? sort!.ToLowerInvariant() : Relevance;
}

public record SearchResponse(
    List<SearchResultDto> Results,
    int TotalHits,
    long ElapsedMs);
