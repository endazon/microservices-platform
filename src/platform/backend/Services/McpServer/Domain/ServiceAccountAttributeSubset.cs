using McpServer.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace McpServer.Domain;

// 🔴 FR-16, FR-05, UC-09, SC-12, ADR-0062 決定 2: 無人アカウントへ割り当てられる
// 機密区分（`clearance`）とタグの集合は、**登録操作を行った利用者自身が持つ集合の部分集合**でなければならない。
//
// ■ 🔴 **順序の語を持ち込まない**
//   計画 06_technical/07_abac-attribute-model は序数比較を意図的に排除しており（ADR-0036 D-04 と
//   同じ語彙）、**「`confidential` は `internal` の上か」という問いに本システムは答えを持たない。**
//   したがって「上限を超えない」という比較は書けない。**集合帰属だけで書く。**
//   `clearance` の階段はポリシー（各段の許可集合の明示列挙）にしか存在しないため、
//   登録者が配れる集合は**評価器から引く**（`IRegistrarAttributeResolver`）。ここには表を持たない。
//
// ■ 対象は `clearance` と**タグ**の 2 つだけである
//   ADR-0062 決定 2 が名指しするのがこの 2 つだからである。`department` を跨いだ割当も同型の
//   昇格になり得るが、**計画が決めていないものを実装側で足さない**（作業仕様書 §計画書との差異）。
//
// ■ 判定を HTTP から切り離してある
//   値集合の突き合わせそのものを器を起こさずに試験できるようにするためである
//   （`UserAssignmentValidation` / SC-12 の語彙モジュールと同じ理由）。
public static class ServiceAccountAttributeSubset
{
    // 属性キーの綴りは属性辞書（SC-09）と判定側が読むクレームに合わせる。
    // `clearance` は `BffScopeResolver.ExtractUserAttributes` が読む 2 キーの片方である。
    public const string ClearanceKey = "clearance";
    // 綴りは契約側の語彙（集合値キーの正）から引く。**ここへ文字列を写さない**（IADR-0386 / #1243）。
    public const string TagsKey = UserAttributeEncoding.TagsKey;

    /// <summary>本規則が見る属性キー。**ここに無いキーは本規則の対象外である。**</summary>
    public static IReadOnlyList<string> GovernedKeys { get; } = [ClearanceKey, TagsKey];

    /// <summary>
    /// 要求された属性が本規則の対象を含むか。
    ///
    /// 🔴 **含まないなら登録者の解決を呼ばない。** 呼ぶと「属性を持たない無人アカウントの登録」まで
    /// 認可サービスの可用性に従属し、**本規則と無関係な経路が落ちる**。
    /// </summary>
    public static bool Governs(IReadOnlyDictionary<string, string> attributes)
        => GovernedKeys.Any(attributes.ContainsKey);

    /// <summary>
    /// 部分集合の判定。外れた値を**名指しで**含むエラーを返す（空なら妥当）。
    ///
    /// ADR-0062 §結果:「後段の拒否応答に理由（どの値が部分集合から外れたか）を含めること」。
    /// 🔴 **「権限がありません」で丸めない。** 丸めると、画面が事前に示せないことの緩和策が消える。
    /// 🔴 **外れていない値を混ぜない**（差集合だけを名指しする）。
    /// </summary>
    public static IReadOnlyList<string> Validate(
        string clientId,
        IReadOnlyDictionary<string, string> attributes,
        RegistrarAssignableAttributes registrar)
    {
        if (!Governs(attributes)) return [];

        // 引けなかったときは**何も配らない**。「値が悪い」と混ぜず、引けなかったことをそのまま書く
        // （混ぜると、認可サービスの障害が「あなたはその区分を持っていません」という嘘の理由になる）。
        if (!registrar.Available)
        {
            return
            [
                $"サービスアカウント '{clientId}' の属性を検証できません"
                + "（登録者の ABAC 属性を認可サービスから解決できませんでした）。"
                + "時間をおいて再実行してください。"
            ];
        }

        var errors = new List<string>();

        if (attributes.TryGetValue(ClearanceKey, out var clearance)
            && !registrar.ClearanceUnrestricted)
        {
            var outside = Difference(clearance, registrar.Clearance);
            if (outside.Count > 0)
            {
                errors.Add(
                    $"{ClearanceKey} の値 {Join(outside)} は割り当てられません"
                    + $"（登録者が持つ機密区分は {Describe(registrar.Clearance)}）。");
            }
        }

        if (attributes.TryGetValue(TagsKey, out var tags))
        {
            var outside = Difference(tags, registrar.Tags);
            if (outside.Count > 0)
            {
                errors.Add(
                    $"{TagsKey} の値 {Join(outside)} は割り当てられません"
                    + $"（登録者が持つタグは {Describe(registrar.Tags)}）。");
            }
        }

        return errors;
    }

    /// <summary>
    /// 属性値をトークンの集合として読む。
    ///
    /// 契約は 1 キー 1 値であり（<c>Dictionary&lt;string,string&gt;</c>）、計画の「タグの**集合**」を
    /// 運ぶ器が他に無い。**単一値はその 1 要素の集合になる**ので、`clearance` の読み方は変わらない。
    ///
    /// 🔴 **分割規則そのものは契約側（<see cref="UserAttributeEncoding.Split"/>）に 1 つだけ置く**
    /// （IADR-0386 / #1243）。従前はここが唯一の分割規則で、上流（Keycloak の多値属性を
    /// 先頭 1 値へ畳む写像）と**食い違っていた** —— 誰も作れない形を待っていた。
    /// AuthorizationService と本サービスは互いを直接参照できないため、規則を各側へ写すと
    /// **その食い違いをそのまま再生産する。**
    ///
    /// 🔴 **順序は意味を持たない**（集合であって列ではない）。`"hr,sales"` と `"sales,hr"` は同値である。
    /// </summary>
    public static IReadOnlySet<string> Tokens(string? value) => UserAttributeEncoding.Split(value);

    // 要求された値のうち、登録者が持たないもの。**入力の綴りをそのまま返す**（画面へ出すため）。
    private static IReadOnlyList<string> Difference(string requested, IReadOnlySet<string> owned)
        => [.. Tokens(requested).Where(v => !owned.Contains(v)).OrderBy(v => v, StringComparer.Ordinal)];

    private static string Join(IEnumerable<string> values)
        => string.Join(", ", values.Select(v => $"'{v}'"));

    // **空集合を「なし」と書く。**「'' です」と書くと値が空文字であるかのように読める。
    private static string Describe(IReadOnlySet<string> values)
        => values.Count == 0
            ? "ありません"
            : $"{Join(values.OrderBy(v => v, StringComparer.Ordinal))} です";
}
