namespace AuthorizationService.Domain;

// FR-05, FR-19, NFR-09, 計画 ADR-0121 決定 1・2, ADR-0036 D-02, [[IADR-0480]], [[IADR-0481]] (#1665):
// **所有者の読み取りのポリシーが「在る」かの判定。** 内容の ABAC の門と、消えたときの検知の両方がこれだけを使う。
//
// 真になるのは次をすべて満たすポリシーだけである（IADR-0480 決定 1 の形）:
//   - 有効（`IsActive`）
//   - 動作が `read`（評価器と同じ序数比較。`Read` は評価器に拾われないので所有者の分岐として働かない）
//   - 利用者の条件が 0 キー
//   - 文書の条件がちょうど 1 キーで、キーが `owner`、値の集合が `{${current_user}}`
//
// 🔴 **形が近いが違うポリシーを「在る」と数えない。** 数え違いは門を誤って開き、消失の警報を黙らせる。
//   - 利用者の条件のキーが在れば、値が空でも「条件あり」である（空の許可値は誰にも一致しない ＝ 全員に効く所有者の分岐ではない）。
//   - 文書の条件のキーは序数で比べる。消費側の述語は `owner` を序数で引くので、`Owner` のポリシーは所有者の分岐として働かない。
//   - 値に他の値（利用者名・`${current_groups}`）が混ざれば、それは所有者の分岐より**広い**別のポリシーである。
//   - 同じ `${current_user}` の重複だけは同値として認める（評価器は束縛で 1 値にする）。
public static class OwnerReadPolicyShape
{
    public const string OwnerKey = "owner";
    public const string CurrentUserPlaceholder = "${current_user}";

    public static bool Matches(AbacPolicy policy)
    {
        if (!policy.IsActive) return false;
        if (!string.Equals(policy.Action, PolicyAction.Read, StringComparison.Ordinal)) return false;
        if (policy.UserConditions is { Count: > 0 }) return false;

        var doc = policy.DocumentConditions;
        if (doc is not { Count: 1 }) return false;

        var (key, values) = doc.First();
        if (!string.Equals(key, OwnerKey, StringComparison.Ordinal)) return false;
        return values is { Count: > 0 }
               && values.All(v => string.Equals(v, CurrentUserPlaceholder, StringComparison.Ordinal));
    }

    /// <summary>形の合う有効なポリシーの件数。0 が「無い」である。</summary>
    public static int CountActive(IEnumerable<AbacPolicy> policies) => policies.Count(Matches);
}
