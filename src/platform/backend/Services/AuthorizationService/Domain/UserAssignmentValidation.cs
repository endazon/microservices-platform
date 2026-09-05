using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Domain;

// FR-05, FR-09, UC-05, SC-17, ADR-0026, IADR-0301: 利用者への割当（ロール・ABAC 属性）の検証。
// 計画 05_screens §SC-17 §入力/バリデーション を写像する。
//
// 🔴 **判定を HTTP から切り離してある。** 値集合と必須判定そのものを、器を起こさずに試験できる
// ようにするためである（SC-12 の語彙モジュールと同じ理由。IADR-0129 決定 6）。
public static class UserAssignmentValidation
{
    /// <summary>
    /// 計画 05_screens §SC-17:「ABAC属性（部門・機密区分上限）＝**必須**」。
    ///
    /// 🔴 **辞書の <c>Required</c> 列から引かない。** 同列は**取り込み時の必須性**として運用されており
    /// （<c>deploy/local/abac-seed/attributes.json</c> の注記:「required は**すべて false** …
    /// 必須化は実データ側が属性を備えてから行う」）、**割当の必須性とは別の軸**である。
    /// 1 つの列を 2 つの意味で使うと、片方を直したときにもう片方が黙って緩む。
    ///
    /// キーが `department` / `clearance` なのは、**判定側が読むクレームがこの 2 つだから**である
    /// （realm の `abac-attributes` スコープ → <c>BffScopeResolver.ExtractUserAttributes</c>）。
    /// 計画の「部門」「機密区分上限」がこの 2 キーへ落ちる。
    /// </summary>
    public static readonly string[] RequiredUserAttributeKeys = ["department", "clearance"];

    /// <summary>
    /// ロール割当の検証（必須・複数選択・**定義済みロールのみ**・併任可）。
    /// <paramref name="assignable"/> は IdP から引いた値域であり、**呼び出し側が焼き込まない**。
    /// </summary>
    public static List<string> ValidateRoles(
        IReadOnlyList<string>? roles, IReadOnlyCollection<string> assignable)
    {
        var errors = new List<string>();

        // 05_screens §SC-17: ロール割当は**必須**である。空集合は「権限を全部剥がす」であって
        // 「未入力」ではない —— 剥奪が要るなら無効化（disable）を使う。
        if (roles is null || roles.Count == 0)
        {
            errors.Add("roles は必須です（1 件以上のロールを割り当ててください）。");
            return errors;
        }

        var blank = roles.Where(string.IsNullOrWhiteSpace).ToList();
        if (blank.Count > 0)
            errors.Add("roles に空のロール名を含めることはできません。");

        var duplicated = roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .GroupBy(r => r, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicated.Count > 0)
            errors.Add($"roles に重複があります: {string.Join(", ", duplicated)}");

        // 「定義済みロールのみ」。**値域は IdP が持つ**（画面にも後段にも焼き込まない）。
        var unknown = roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Where(r => !assignable.Contains(r, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
            errors.Add(
                $"定義済みでないロールは割り当てられません: {string.Join(", ", unknown)}"
                + $"（割当可能: {string.Join(", ", assignable)}）。");

        return errors;
    }

    /// <summary>
    /// 利用者へ割り当てる ABAC 属性の検証（必須充足 ＋ 辞書整合）。
    ///
    /// 🔴 **文書側（<c>AbacValidation.ValidateDocumentAttributes</c>）と 1 点だけ意図的に違う** ——
    /// **辞書に無いキーを拒否する**。文書側は自由タグを許容するが、利用者側の属性は
    /// **認可判定の主体側の入力**であり、辞書外のキーを受けても判定には一切効かない
    /// （<c>BffScopeResolver</c> が読むのは辞書由来のクレームだけである）。
    /// 受け付けて無視すると「割り当てたのに効かない」が黙って作れてしまうので、その場で断る。
    /// 計画 05_screens §SC-17 も「SC-09 の属性体系・タグ辞書に**定義済みの値のみ**」と定めている。
    /// </summary>
    public static List<string> ValidateAttributes(
        IReadOnlyDictionary<string, string>? attributes,
        IEnumerable<AttributeDefinition> definitions)
    {
        var errors = new List<string>();
        var attrs = attributes ?? new Dictionary<string, string>();
        var userDefs = definitions
            .Where(d => string.Equals(d.Scope, AttributeScope.User, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 必須の充足（部門・機密区分上限）。**辞書に無ければ「辞書側が未整備」として断る**
        // —— 必須を黙って諦めると、統制を定めたことと効いていることの区別が消える。
        foreach (var key in RequiredUserAttributeKeys)
        {
            var def = userDefs.FirstOrDefault(d =>
                string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (def is null)
            {
                errors.Add(
                    $"必須属性 '{key}' が利用者スコープの属性辞書に定義されていません"
                    + "（SC-09 の属性体系へ登録してください）。");
                continue;
            }

            if (!attrs.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                errors.Add($"必須属性 '{key}' が未設定です。");
        }

        foreach (var (key, value) in attrs)
        {
            var def = userDefs.FirstOrDefault(d =>
                string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (def is null)
            {
                errors.Add($"属性 '{key}' は利用者スコープの属性辞書に定義されていません。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                // 任意属性（タグ等）を「外す」ときは、空文字ではなくキーごと送らない
                // （差し替えなので、送らなければ消える）。空文字を保存すると辞書外の値になる。
                errors.Add($"属性 '{key}' の値が空です（外すときはキーごと送らないでください）。");
                continue;
            }

            // IADR-0386 (#1243): **集合値キー（tags / projects）は要素ごとに突き合わせる。**
            // 値全体で見ると `"sales,hr"` は決して許可値にならず、**画面から集合を作れない**
            // （その集合を部分集合判定〔ADR-0062 決定 2〕が読む先で待っている）。
            // 🔴 **単一値キーの判定は 1 文字も変えない** —— `clearance` の値に区切り文字を
            // 見出すと、辞書外の値が要素として通り得る。
            foreach (var element in UserAttributeEncoding.IsSetValued(key)
                ? UserAttributeEncoding.SplitOrdered(value)
                : [value])
            {
                if (!def.AllowedValues.Contains(element, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"属性 '{key}' の値 '{element}' は許可値に含まれません。");
            }
        }

        return errors;
    }
}
