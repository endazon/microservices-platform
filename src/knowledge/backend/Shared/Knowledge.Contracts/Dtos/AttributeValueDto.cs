using Platform.Shared.Contracts.Dtos;

namespace Knowledge.Contracts.Dtos;

// FR-04, FR-05, SC-01, SC-08, #540: 権限内属性値の照会（計画 ADR-0043・裁定 Q2）。
//
// SC-01 / SC-08 の対象範囲フィルタは「**権限内のタグ／部門／プロジェクトのみ選択可**」であり、
// 一般利用者が呼べる候補一覧が要る。**返す範囲には強い制限がある**（ADR-0043）——
// 外すと存在秘匿（ADR-0004 の 404 原則）が実質的に破れる。
public record AttributeValuesRequest(
    // 引きたい属性の種別。`tags` または ABAC 属性キー（`department` 等）。
    // 未知・空は空集合へ縮退する（呼び出し側の分岐を 1 か所に閉じる）。
    string Key,
    // FR-05: ABAC アクセススコープ。**BFF がサーバ側で解決した値だけを信頼する**
    // （クライアントが指定した Scope を後段が信用すると権限昇格になる。BffScopeResolver と同じ扱い）。
    AccessScope? Scope = null);

// FR-04, SC-01, SC-08, #540: 照会の応答。
//
// **値の配列だけを持つ。件数のフィールドを作らない**（ADR-0043 決定 2 / IADR-0151 決定 2）——
// 「このタグは 12 件だが自分の検索では 8 件」＝**見えない文書が 4 件ある**ことが分かるため、
// **件数は値集合そのものより漏洩力が強い**。実装は Qdrant の facet（件数つき）で数えるが、
// **件数はサービス内で捨てる**。
//
// #542 がシステム管理者スコープ（値集合・使用件数・追加・改名・削除）を**同じ口へ**足すときは、
// **管理者スコープ専用の別フィールド**として足す（一般利用者の応答形を変えない。IADR-0151 決定 4）。
public record AttributeValuesResponse(List<string> Values);

// FR-04, SC-01, SC-08, #540: 引ける種別。
// enum ではなく文字列 + const で持つ（IADR-0131 決定 5。SearchModes / SearchSorts と同じ作法）。
public static class AttributeValueKeys
{
    // タグは Qdrant ペイロードのリスト項目 `tags` に入る。
    public const string Tags = "tags";

    // ABAC 属性はネスト構造体 `attributes -> { k: v }` に入る（IADR-0014 選択肢C・実機検証済み）。
    // facet のキーはドット記法で `attributes.<key>` を指す。
    public const string AttributesPrefix = "attributes";

    // 照会キーを Qdrant ペイロードのキーへ写す。**`tags` だけが例外で、他は属性として扱う。**
    //
    // **大文字小文字の扱いが `tags` と属性キーで違うのは意図的である。**
    // `tags` は**本コードが所有するリテラル**なので、呼び出し側が `Tags` と書いても同じ口へ寄せてよい。
    // 属性キーは違う——**投入時に書いたキーがそのままペイロードのキーになる**（IADR-0014 のネスト構造体）ため、
    // ここで畳むと `Department` が書き込み時の `department` と一致しなくなり、**黙って空集合が返る**。
    // 属性キーの正規化は権限側（属性定義）の責務であり、照会側で勝手に変えない。
    public static string ToPayloadKey(string key) =>
        string.Equals(key, Tags, StringComparison.OrdinalIgnoreCase)
            ? Tags
            : $"{AttributesPrefix}.{key}";
}
