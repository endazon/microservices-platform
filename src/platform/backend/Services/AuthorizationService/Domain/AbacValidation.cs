namespace AuthorizationService.Domain;

// FR-09, UC-05, ADR-0004: 属性辞書・ポリシー・文書属性のバリデーション。
// UC-05 例外フロー「矛盾するポリシーは保存前に検証し、エラーを返す」を写像する。
// 段階導入方針: 属性辞書に「定義済みのキー」のみ許可値整合を検証し、未定義キー（自由タグ等）は許容する。
public static class AbacValidation
{
    // 属性辞書エントリの検証。update 時は excludeId で自分自身を一意チェックから除外する。
    // ［2026-09-27 / #1609・計画 ADR-0116 決定 3］`allowedValuesDerived` が true のキー（`department`）は許可値を
    // realm の部門グループから導くため、要求の許可値の形（1 件以上・重複なし）をここでは見ない
    // （受け付けるかは `DepartmentDictionaryValues.RequestAccepted` が決める）。
    // ［2026-09-28 / #1676］`keyAlreadyStored` は「キー・スコープが既に保存されている属性の更新」（Key / Scope は不変）である。
    // 真なら利用者スコープの束縛の位置の名前の検査（下）だけを飛ばす —— 登録の拒否より前から在る属性のラベル・許可値を直せるようにする。
    public static List<string> ValidateAttributeDefinition(
        string? key, string? label, List<string>? allowedValues, string? scope,
        IEnumerable<AttributeDefinition> existing, Guid? excludeId = null, bool allowedValuesDerived = false,
        bool keyAlreadyStored = false)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(key))
            errors.Add("key は必須です。");
        if (string.IsNullOrWhiteSpace(label))
            errors.Add("label は必須です。");

        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? AttributeScope.Document : scope;
        if (!AttributeScope.IsValid(normalizedScope))
            errors.Add($"scope は {string.Join(" / ", AttributeScope.All)} のいずれかである必要があります。");

        // 導くキーの許可値は呼び出し元が realm のコードへ置き換えるので、形を見ない。
        if (!allowedValuesDerived && (allowedValues is null || allowedValues.Count == 0))
        {
            errors.Add("allowedValues は 1 件以上必要です。");
        }
        else if (!allowedValuesDerived && allowedValues is not null)
        {
            if (allowedValues.Any(string.IsNullOrWhiteSpace))
                errors.Add("allowedValues に空の値を含めることはできません。");
            var dup = allowedValues
                .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Where(grp => grp.Count() > 1)
                .Select(grp => grp.Key)
                .ToList();
            if (dup.Count > 0)
                errors.Add($"allowedValues に重複があります: {string.Join(", ", dup)}");
        }

        // FR-05, SC-09 (#1666 レビュー): **利用者スコープに束縛の位置と同名のキー（owner・shared_with）を作らせない。**
        // 束縛は文書の条件にだけ置けるので、利用者属性の `owner` は SC-09 の条件エディタで文書の `owner`（束縛）と
        // 名前だけが同じ別物として並び、取り違えの元になる。辞書のキーの同一性は大小を区別しない（下の一意の検査と同じ）ので、
        // ここも大小を区別せずに拒む。文書スコープの同名は拒まない（束縛の値と辞書の許可値を併せて持てる）。
        // ［2026-09-28 / #1676］**拒むのは登録だけである。** 更新では Key / Scope が変わらないので、この検査で新たに
        // 同名の利用者属性が生まれることは無い。更新まで拒むと、拒否より前に登録された属性はラベルの変更すら 400 になり、
        // 参照中なら削除もできない（409）ので直す手段が無くなる。
        if (!keyAlreadyStored
            && !string.IsNullOrWhiteSpace(key)
            && string.Equals(normalizedScope, AttributeScope.User, StringComparison.OrdinalIgnoreCase)
            && DynamicBindingKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(
                $"key '{key}' は利用者属性に使えません。owner・shared_with は文書の条件で動的束縛を置く位置の名前です。");
        }

        // 同一スコープ内でキーは一意（辞書としての整合）。
        if (!string.IsNullOrWhiteSpace(key)
            && existing.Any(a => a.Id != excludeId
                && string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Scope, normalizedScope, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"key '{key}' は scope '{normalizedScope}' に既に定義済みです。");
        }

        return errors;
    }

    // ABAC ポリシーの検証。定義済みキーのみ許可値整合を検証する（段階導入）。
    public static List<string> ValidatePolicy(
        string? name, string? action,
        Dictionary<string, List<string>>? userConditions,
        Dictionary<string, List<string>>? documentConditions,
        IEnumerable<AttributeDefinition> definitions)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(name))
            errors.Add("name は必須です。");

        if (string.IsNullOrWhiteSpace(action) || !PolicyAction.IsValid(action))
            errors.Add($"action は {string.Join(" / ", PolicyAction.All)} のいずれかである必要があります。");

        var defList = definitions.ToList();
        ValidateConditions("userConditions", userConditions, defList, AttributeScope.User, action, errors);
        ValidateConditions("documentConditions", documentConditions, defList, AttributeScope.Document, action, errors);
        if (!IsAstKbReaderCeilingPolicy(action, userConditions, documentConditions))
            ValidateSingleDocumentConditionKey(documentConditions, errors);
        ValidateDynamicBindings(action, userConditions, documentConditions, errors);

        return errors;
    }

    // FR-05, SC-09, 計画 ADR-0036 D-02・D-03・D-06・D-07, ADR-0121 決定 1 (#1666): 動的束縛を置いてよい (action, key, 変数) の組。
    //
    // 計画が束縛を置くのは**文書の条件の次の組だけ**である（07_abac-attribute-model §動的束縛 の判定規則）。
    //   read  の所有者ベース: doc.owner ∈ { ${current_user} }
    //   read  の共有先ベース: doc.shared_with ∩ ({${current_user}} ∪ ${current_groups}) ≠ ∅
    //   write              : doc.owner ∈ { ${current_user} }（「共有先には書き込み権限を与えない」）
    // analyze・manage の判定規則に束縛は無い。
    // SC-09 の編集器は同じ表から選択肢を作る（`abacVocabulary.ts` の `DYNAMIC_BINDINGS`）。
    // 🔴 **語彙は計画が定める。** 組を足すなら計画 ADR の側で定義してから両方に足す（`AbacEvaluator` と同じ趣旨）。
    //
    // ［2026-09-28 / #1666 監査］**action の次元を持つ。** 持たないと `write` × `shared_with:[${current_user}]` が通り、
    // 境界層（`BffScopeResolver` の write スコープ）が共有先に書き込みを許す。
    //
    // ［2026-09-28 / #1666 レビュー］**キーは大小を区別する（Ordinal）。** 文書の属性の突き合わせは大小を区別する
    // （`AttributeFilterMatch.MatchesAll` の `TryGetValue`・`DocumentAttributeEncoding.WithSharedWith` の Ordinal）ので、
    // `Owner ∈ {${current_user}}` は `owner` を持つ文書に一致しない＝保存できても静かに効かない。画面の表（`DYNAMIC_BINDINGS`）も
    // 大小を区別する。変数（値）も評価器が完全一致でしか束縛しないので Ordinal である。
    private static readonly Dictionary<string, Dictionary<string, string[]>> DynamicBindingPositions =
        new(StringComparer.Ordinal)
        {
            [PolicyAction.Read] = new(StringComparer.Ordinal)
            {
                ["owner"] = ["${current_user}"],
                ["shared_with"] = ["${current_user}", "${current_groups}"],
            },
            [PolicyAction.Write] = new(StringComparer.Ordinal)
            {
                ["owner"] = ["${current_user}"],
            },
        };

    // 束縛の位置になり得るキー（action を問わない）。利用者属性の名前の検査に使う。
    private static readonly string[] DynamicBindingKeys =
        [.. DynamicBindingPositions.Values.SelectMany(p => p.Keys).Distinct(StringComparer.Ordinal)];

    // 束縛の形かどうか。`${` を含む値は束縛として書かれたものとみなす（リテラルに `${` を含む属性値は無い）。
    // 前後に文字が付いたもの（`x${current_user}`・`${current_user} `）も束縛の形であり、表の値と完全一致しないので拒否される。
    private static bool LooksLikeBinding(string? value) =>
        value is not null && value.Contains("${", StringComparison.Ordinal);

    // 文書の条件の (action, key, value) が、計画が定める束縛の組に当たるか。
    // 変数名は**大小を区別する** —— 評価器は `${current_user}` を完全一致でしか束縛しない（`AbacEvaluator.BindPlaceholders`）。
    private static bool IsAllowedDocumentBinding(string? action, string key, string value) =>
        action is not null
        && DynamicBindingPositions.TryGetValue(action, out var positions)
        && positions.TryGetValue(key, out var allowed)
        && allowed.Contains(value, StringComparer.Ordinal);

    // FR-05, SC-09, ADR-0036 D-03 (#1666): 動的束縛の検証。
    //
    // 🔴 **止めるのは「保存できるが静かに効かないポリシー」と「計画より広い許可」である。** 評価器は未知のプレースホルダを
    // リテラルのまま残す（`${current_usr}` はどの文書にも一致しない）。利用者の条件は束縛しない
    // （`${current_user}` を利用者の条件へ置くと、その文字列を属性に持つ利用者にしか一致しない）。
    // 計画に無い action の束縛（write × shared_with）と、束縛とリテラルの混在（`owner:[${current_user}, "bob"]` は
    // 全員に bob の文書を許す）は許可を広げる。どれも誤りとして表に出ないので、保存の前に止める。
    private static void ValidateDynamicBindings(
        string? action,
        Dictionary<string, List<string>>? userConditions,
        Dictionary<string, List<string>>? documentConditions,
        List<string> errors)
    {
        foreach (var (key, values) in userConditions ?? [])
        {
            foreach (var value in (values ?? []).Where(LooksLikeBinding))
            {
                errors.Add(
                    $"userConditions.{key} に動的束縛 '{value}' は置けません。"
                    + "動的束縛は文書の条件（owner・shared_with）にだけ置けます。");
            }
        }

        foreach (var (key, values) in documentConditions ?? [])
        {
            var list = values ?? [];
            var bindings = list.Where(LooksLikeBinding).ToList();
            foreach (var value in bindings)
            {
                if (IsAllowedDocumentBinding(action, key, value))
                    continue;
                errors.Add(
                    $"documentConditions.{key} に動的束縛 '{value}' は置けません（action={action}）。"
                    + "置けるのは read の owner の ${current_user}・shared_with の ${current_user}・${current_groups} と、"
                    + "write の owner の ${current_user} だけです。");
            }

            // 束縛の位置の値は束縛だけで作る（計画の owner の位置は { ${current_user} } だけ）。
            if (bindings.Count > 0 && bindings.Count < list.Count)
            {
                var literals = list.Where(v => !LooksLikeBinding(v));
                errors.Add(
                    $"documentConditions.{key} に動的束縛とリテラル（{string.Join(", ", literals)}）を混ぜることはできません。"
                    + "リテラルを混ぜると、束縛の条件と無関係にその値の文書が全員に許可されます。");
            }
        }
    }

    // FR-05, FR-09, SC-09（planning#470 の裁定・2026-08-23）: **文書条件に 2 つ以上の属性キーを
    // 持つポリシーの保存を拒否する。**
    //
    // 🔴 **理由は評価器の潰し方にある。** 認可スコープ契約は選言（ポリシー単位の連言の OR）を
    // 運べないため、`AbacEvaluator` は**マッチした全ポリシーの文書条件をキー単位 union で
    // 1 本の連言へ潰す**。その結果、多キーポリシーが複数マッチすると
    // **どのポリシー単独も許可しない値の混成が許可される**。
    //
    //   P1: { dept: [sales], conf: [public] }   P2: { dept: [hr], conf: [confidential] }
    //   union → { dept: [sales, hr], conf: [public, confidential] }
    //   → (dept=sales, conf=confidential) が通る。**P1 も P2 も許可していない組合せである。**
    //
    // 🔴 **これは暫定であり、恒久の制限ではない。** 消費側が選言（ポリシー単位の連言）へ
    // 対応した時点で本検証を外す。**今日漏れていないのは実効軸が confidentiality 1 本だから**
    // であって、統制が効いているからではない。
    //
    // **利用者条件は対象外である** —— 潰しているのは文書条件の側だけであり、利用者条件は
    // 「すべて満たすか」の判定にしか使われない。
    // FR-05, FR-09, 計画 ADR-0125 決定 1・2, [[IADR-0500]] 決定 4 (#1755):
    // **上の「文書条件は 1 キーまで」の、ただ 1 つの例外。** AST の KB の読み手の read ポリシーは、
    // 文書の条件を `project ∈ {ai-stock-trading}` ∧ `confidentiality ⊆ {public, internal}` の 2 キーで持つ
    // （ADR-0125 決定 2 の機密区分の上限。1 キーずつのポリシーに分けると和になり、上限が効かない）。
    //
    // 🔴 **形を値まで固定するのは、例外がキー単位 union の過剰許可を生まないための条件だからである。**
    //   例外に当たるポリシーはどれも `project` の値が `ai-stock-trading` 1 つで、機密区分は public・internal の部分集合である。
    //   したがって、これらと 1 キーのポリシーをキー単位で潰しても、通る文書（`project=ai-stock-trading` かつ区分 v）には
    //   v を許した例外のポリシーか、v を許した機密区分 1 キーのポリシーが**単独で**許可を与えている —— planning#470 の
    //   「どのポリシー単独も許可しない混成」は生じない。値を広げる（別の project・confidential 以上）と、この論証が崩れる。
    // 🔴 **利用者の条件も固定する**（`projects ∋ ai-stock-trading` だけ。ADR-0125 決定 1 の条件 1〜3。clearance を足すと
    //   階段の段と並んで同じ主体にマッチする）。action は read だけ（同 条件 4）。
    private static readonly string[] AstKbReaderCeiling = ["public", "internal"];

    internal static bool IsAstKbReaderCeilingPolicy(
        string? action,
        Dictionary<string, List<string>>? userConditions,
        Dictionary<string, List<string>>? documentConditions)
    {
        if (!string.Equals(action, PolicyAction.Read, StringComparison.Ordinal)) return false;
        if (userConditions is not { Count: 1 } || documentConditions is not { Count: 2 }) return false;
        if (!IsExactly(userConditions, "projects", out var projects)
            || projects is not ["ai-stock-trading"]) return false;
        if (!IsExactly(documentConditions, "project", out var project)
            || project is not ["ai-stock-trading"]) return false;
        if (!IsExactly(documentConditions, "confidentiality", out var ceiling)
            || ceiling.Count == 0 || ceiling.Distinct(StringComparer.Ordinal).Count() != ceiling.Count
            || !ceiling.All(v => AstKbReaderCeiling.Contains(v, StringComparer.Ordinal))) return false;
        return true;

        static bool IsExactly(Dictionary<string, List<string>> conditions, string key, out List<string> values)
        {
            values = [];
            if (!conditions.TryGetValue(key, out var found) || found is null) return false;
            values = found;
            return true;
        }
    }

    private static void ValidateSingleDocumentConditionKey(
        Dictionary<string, List<string>>? documentConditions, List<string> errors)
    {
        if (documentConditions is null || documentConditions.Count <= 1) return;

        errors.Add(
            "documentConditions に指定できる属性キーは 1 つまでです"
            + $"（指定: {string.Join(", ", documentConditions.Keys)}）。"
            + "認可スコープが選言を運べないため、評価器は複数ポリシーの文書条件をキー単位で"
            + "統合します。2 つ以上のキーを持つポリシーが複数マッチすると、"
            + "どのポリシー単独も許可しない値の組合せが許可されてしまいます。"
            + "キーごとにポリシーを分けてください。"
            + "（この制限は暫定です。認可スコープが選言に対応した時点で解除されます。）");
    }

    private static void ValidateConditions(
        string field, Dictionary<string, List<string>>? conditions,
        IReadOnlyCollection<AttributeDefinition> definitions, string scope, string? action, List<string> errors)
    {
        if (conditions is null)
            return;

        foreach (var (key, values) in conditions)
        {
            if (values is null || values.Count == 0)
            {
                errors.Add($"{field}.{key} の値集合は空にできません。");
                continue;
            }

            // 辞書に定義済みのキーのみ許可値整合を検証（未定義キーは許容）。
            var def = definitions.FirstOrDefault(d =>
                string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Scope, scope, StringComparison.OrdinalIgnoreCase));
            if (def is null)
                continue;

            // #1666: 計画が定める位置の束縛は辞書の値ではない（利用者名・グループ ID は列挙できない）。
            // 辞書に owner・shared_with が定義されていても「辞書外」として拒否しない（位置の検証は ValidateDynamicBindings）。
            var invalid = values
                .Where(v => !def.AllowedValues.Contains(v, StringComparer.OrdinalIgnoreCase))
                .Where(v => !(scope == AttributeScope.Document && IsAllowedDocumentBinding(action, key, v)))
                .ToList();
            if (invalid.Count > 0)
                errors.Add($"{field}.{key} に辞書外の値があります: {string.Join(", ", invalid)}");
        }
    }

    // FR-09, IADR-0006: 指定ポリシーが当該属性キー（scope 一致）を条件に参照しているか。
    // 属性辞書の削除可否判定に用いる（参照中の削除は制約緩みを招くため拒否する）。
    public static bool PolicyReferencesAttribute(AbacPolicy policy, string key, string scope)
    {
        var conditions = string.Equals(scope, AttributeScope.User, StringComparison.OrdinalIgnoreCase)
            ? policy.UserConditions
            : policy.DocumentConditions;
        return conditions is not null
            && conditions.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
    }

    // 文書に付与する属性値が辞書と整合するか検証する（必須充足＋許可値整合）。
    public static List<string> ValidateDocumentAttributes(
        Dictionary<string, string>? attributes,
        IEnumerable<AttributeDefinition> definitions)
    {
        var errors = new List<string>();
        var attrs = attributes ?? new Dictionary<string, string>();
        var docDefs = definitions
            .Where(d => string.Equals(d.Scope, AttributeScope.Document, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 必須属性の充足。
        foreach (var def in docDefs.Where(d => d.Required))
        {
            if (!attrs.TryGetValue(def.Key, out var value) || string.IsNullOrWhiteSpace(value))
                errors.Add($"必須属性 '{def.Key}' が未設定です。");
        }

        // 許可値整合（定義済みキーのみ検証、未定義キーは自由タグとして許容）。
        foreach (var (key, value) in attrs)
        {
            var def = docDefs.FirstOrDefault(d =>
                string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (def is null)
                continue;
            if (!def.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
                errors.Add($"属性 '{key}' の値 '{value}' は許可値に含まれません。");
        }

        return errors;
    }
}
