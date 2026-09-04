namespace McpServer.Domain;

// 🔴 FR-16, UC-08 例外フロー, UC-09, SC-12, AST/ADR-0032 決定 2・決定 3 (#1190):
// **MCP の外部エージェント経路から一律に外すプロジェクトコードの語彙。**
//
// `DocumentScope`（個人資料）と同じ形にしてある —— 1 ファイル 1 語彙・**集合帰属で判定**・
// 判定の向きを 2 箇所に持たない。読む人が覚える形を 1 つに保つためである。
//
// ■ 属性キーが 2 つあるのは綴りが 2 つあるからである
//   計画 06_technical/07_abac-attribute-model は **文書側を単数 `project`（プロジェクトコード）**、
//   **主体側を複数 `projects`（参加プロジェクト。Keycloak／グループ由来）** と定めている。
//   AST/ADR-0032 決定 2 (2) は「サービスアカウントの `projects` に `ai-stock-trading` を入れない」と
//   書き、#1190 の本文は `attributes["project"]` と書く。**片方だけ塞ぐと綴りを変えるだけで抜ける**
//   ので、割当の検査は両方を見る。
//
// ■ 🔴 値の集合を構成（appsettings）から読まない
//   読めるようにすると **統制を無効化する抜け道**になる（check-stack-ready.js G3「抜け道の
//   環境変数を置かない」と同じ理由）。ユニットが増えたら本ファイルの 1 箇所へ足す。
public static class RestrictedProject
{
    /// <summary>文書側の属性キー（07_abac-attribute-model §基本属性）。</summary>
    public const string DocumentKey = "project";

    /// <summary>主体側の属性キー（同 §利用者属性）。サービスアカウントへの割当はこちらが正。</summary>
    public const string SubjectKey = "projects";

    /// <summary>
    /// MCP のサービスアカウント経路から一律に外すプロジェクトコード。
    /// **大文字小文字を問わない**（realm・取り込み経路で綴りが揺れても同じ値と読む）。
    /// </summary>
    public static IReadOnlySet<string> Values { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ai-stock-trading" };

    /// <summary>
    /// 文書が制限対象のプロジェクトに属するか。
    ///
    /// 🔴 **集合帰属で判定する。「制限値でない」と否定で書いてはならない。**
    /// `project` は任意属性であり、付いていない文書のほうが圧倒的に多い（07_abac-attribute-model
    /// §必須指定と実データの乖離）。否定で書くと**属性を持たない組織文書が一斉に落ちる**。
    /// `DocumentScope.IsPrivateNote` が同じ理由で同じ向きを採っている。
    /// </summary>
    public static bool IsRestricted(IReadOnlyDictionary<string, string> attributes)
        => attributes.TryGetValue(DocumentKey, out var value) && ContainsRestricted(value);

    /// <summary>
    /// 主体（サービスアカウント）への属性割当のうち、制限対象に当たる値を**入力の綴りのまま**返す。
    ///
    /// **理由を丸めない**（ADR-0062 §結果）——どの値が外れたかを拒否応答へ載せるため、
    /// 真偽値ではなく値そのものを返す。多値の分解は `ServiceAccountAttributeSubset.Tokens` を
    /// 再利用する（分割規則を 2 つ持たない）。
    /// </summary>
    public static IReadOnlyList<string> AssignedValues(IReadOnlyDictionary<string, string> attributes)
    {
        var found = new List<string>();
        foreach (var key in new[] { SubjectKey, DocumentKey })
        {
            if (!attributes.TryGetValue(key, out var value)) continue;
            found.AddRange(ServiceAccountAttributeSubset.Tokens(value).Where(Values.Contains));
        }
        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.Ordinal)];
    }

    private static bool ContainsRestricted(string value)
        => ServiceAccountAttributeSubset.Tokens(value).Any(Values.Contains);
}
