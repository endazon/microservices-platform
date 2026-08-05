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
    string? Mode = null);

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

public record SearchResponse(
    List<SearchResultDto> Results,
    int TotalHits,
    long ElapsedMs);
