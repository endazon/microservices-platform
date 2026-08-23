namespace DocumentService.Api.Foundation.Domain;

// FR-05, FR-06, UC-03, SC-05, IADR-0047: 文書属性の必須検証（最終防衛線）。
// 機密区分（confidentiality）は SC-05 / UC-03 例外フローで必須。フロントの select 既定値に依存せず、
// サービス側で欠落・未知値を拒否する（BFF 迂回の直接呼び出しでも実効化する。IADR-0044 の多層防御と整合）。
public static class DocumentAttributes
{
    // FR-05: 機密区分の属性キー。
    public const string ConfidentialityKey = "confidentiality";

    // FR-05: 機密区分の正準値集合。AuthorizationService の AttributeDefinition.AllowedValues と一致させる
    // （["public","internal","confidential","restricted"]）。動的な属性辞書照合は IADR-0047 で見送り、
    // ここでは静的な正準集合で検証する。
    public static readonly IReadOnlySet<string> AllowedConfidentiality =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "public",
            "internal",
            "confidential",
            "restricted",
        };

    // FR-19, ADR-0054 決定 1・2: 文書スコープの属性キーと 2 値。綴りは計画が確定させたもの
    // （ハイフン込み）をそのまま用いる（実装で言い換えない）。
    public const string DocScopeKey = "doc_scope";
    public const string DocScopePrivateNote = "private-note";
    public const string DocScopeOrganization = "organization";

    public static readonly IReadOnlySet<string> AllowedDocScope =
        new HashSet<string>(StringComparer.Ordinal)
        {
            DocScopePrivateNote,
            DocScopeOrganization,
        };

    // FR-19, ADR-0054, [[IADR-0270]] 決定 2: doc_scope の値域検証。
    // 🔴 **欠落は拒否しない** —— 既存 2,368 件は遡及付与しない方針（ADR-0054 §結果）であり、
    // ここで必須にすると doc_scope を持たない既存文書の更新が一斉に 400 になる。
    // 「必須」の実効化（全文書）は必須属性の系譜（#516 / IADR-0199）が別途扱う。
    public static (bool Ok, string? Error) ValidateDocScope(
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null || !attributes.TryGetValue(DocScopeKey, out var value))
            return (true, null);

        if (!AllowedDocScope.Contains(value))
        {
            return (false,
                $"文書スコープ（doc_scope）の値 '{value}' は不正です。" +
                $"許容値: {string.Join(" / ", AllowedDocScope)}。");
        }

        return (true, null);
    }

    // FR-06, FR-19, ADR-0058 決定 1・2, [[IADR-0278]]: **`doc_scope` は作成時に確定し、以後変更できない。**
    //
    // 🔴 **「`doc_scope` を含む更新を拒否」と書いてはならない。** SC-05 の属性編集フォームは
    // 既存属性をスプレッドして送るため、機密区分だけを変える通常の保存でも `doc_scope` が同送される。
    // 存在で弾くと SC-05 の保存が全部壊れる。**既存値と一致しないときだけ拒否する。**
    //
    // 🔴 **属性は全置換である。** したがって「値を変える」だけでなく次の 2 つも変更に当たる。
    //   - **既存値の削除**（`private-note` を落とすと、集合帰属の判定で組織文書へ化ける）
    //   - **後からの新規付与**（作成時に確定するという決定 1 に反する）
    // **一致しないものはすべて拒否する**、という 1 本の規則で 3 つとも閉じる。
    public static (bool Ok, string? Error) ValidateDocScopeUnchanged(
        IReadOnlyDictionary<string, string>? incoming,
        IReadOnlyDictionary<string, string>? current)
    {
        var before = ScopeOrNull(current);
        var after = ScopeOrNull(incoming);
        if (string.Equals(before, after, StringComparison.Ordinal)) return (true, null);

        return (false,
            $"文書スコープ（{DocScopeKey}）は作成時に確定し、以後変更できません" +
            $"（現在: {Describe(before)} / 要求: {Describe(after)}）。" +
            "内容を移したい場合は、移し先の文書スコープで新しい文書を作成してください。");
    }

    private static string? ScopeOrNull(IReadOnlyDictionary<string, string>? attributes)
        => attributes is not null && attributes.TryGetValue(DocScopeKey, out var value)
            ? value
            : null;

    private static string Describe(string? scope) => scope ?? "（未設定）";

    // FR-19, ADR-0046 D-01, ADR-0054, ADR-0036 D-04: 個人資料かどうかの判定。
    // 🔴 **集合帰属（== private-note）で判定する。否定（!= organization）で書くと、属性を持たない
    // 既存の組織文書が一斉に該当する**（実データ 0 件・遡及付与しない方針のため、この向きは
    // 陽性対照テストでしか見分けられない）。WikiService.DocumentSyncConsumer と同一の作法。
    public static bool IsPrivateNote(IReadOnlyDictionary<string, string>? attributes)
        => attributes is not null
            && attributes.TryGetValue(DocScopeKey, out var scope)
            && string.Equals(scope, DocScopePrivateNote, StringComparison.OrdinalIgnoreCase);

    // UC-03 例外フロー / SC-05: 機密区分が付与され、正準値であることを検証する。
    // 未指定・欠落・未知値はいずれも保存拒否（400）とする。
    public static (bool Ok, string? Error) ValidateConfidentiality(
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null ||
            !attributes.TryGetValue(ConfidentialityKey, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return (false, "機密区分（confidentiality）は必須です。");
        }

        if (!AllowedConfidentiality.Contains(value))
        {
            return (false,
                $"機密区分（confidentiality）の値 '{value}' は不正です。" +
                $"許容値: {string.Join(" / ", AllowedConfidentiality)}。");
        }

        return (true, null);
    }
}
