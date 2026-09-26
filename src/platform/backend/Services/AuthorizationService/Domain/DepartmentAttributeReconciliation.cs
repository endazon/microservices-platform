namespace AuthorizationService.Domain;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0115 決定 3, [[IADR-0473]] (#1573):
// **利用者属性 `department` を部門グループの所属に合わせる計画**を立てる純関数（IdP を呼ばない）。
//
// ■ 裁定（計画 ADR-0115 決定 3）
//   利用者の部門の正本は部門グループ（`/department/<code>`）への所属であり、ABAC が読む利用者属性 `department` は
//   それと一致させる。食い違いを見つけたら**属性をグループに合わせて直す。逆向きには直さない。**
//
// ■ 🔴 **ちょうど 1 つの部門に属するときだけ直す。0 個・2 個以上は未解決として上書きしない。**
//   2 部門に属する人の属性を片方へ寄せるのは推測である（データソースの既定部門を導く `RegistrantDepartment` と同じ規則。
//   計画 09_datasource-connectors「安全側は解決しない」）。0 個の人（サービスアカウントを含む）は所属から何も言えない。
//   **どちらも属性を消しもしない。**
//
// ■ 入れ子は上位のコードに畳む（`/department/engineering/backend` の所属者は `engineering`）。同じ部門の入れ子だけで
//   「2 つ」に見えて直せなくなるのを避ける。**照合は序数**（大小文字を区別。Keycloak のグループ名と同じ）。
public static class DepartmentAttributeReconciliation
{
    public const string AttributeKey = "department";
    public const string DepartmentGroupRoot = "/department/";

    /// <summary>
    /// 部門グループのフルパスから部門コードを取り出す（`/department/a/b` → `a`）。部門の木の外・直下が空なら null。
    /// </summary>
    public static string? CodeOf(string? groupPath)
    {
        if (groupPath is null || !groupPath.StartsWith(DepartmentGroupRoot, StringComparison.Ordinal)) return null;
        var rest = groupPath[DepartmentGroupRoot.Length..];
        var slash = rest.IndexOf('/');
        var code = slash < 0 ? rest : rest[..slash];
        return code.Length == 0 ? null : code;
    }

    /// <summary>
    /// 利用者ごとの判定を返す。<paramref name="codesByUser"/> は利用者（IdP の内部 ID）→ 所属する部門コードの集合、
    /// <paramref name="currentByUser"/> は利用者 → 現在の属性 `department`（無ければ null）。
    /// 返り値は内部 ID の序数順（ログと試験を安定させる）。
    /// </summary>
    public static IReadOnlyList<DepartmentAttributeFinding> Plan(
        IReadOnlyDictionary<string, IReadOnlySet<string>> codesByUser,
        IReadOnlyDictionary<string, string?> currentByUser)
    {
        var findings = new List<DepartmentAttributeFinding>(codesByUser.Count);
        foreach (var (userId, codes) in codesByUser.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            currentByUser.TryGetValue(userId, out var current);
            var sorted = codes.OrderBy(c => c, StringComparer.Ordinal).ToArray();

            if (sorted.Length != 1)
            {
                findings.Add(new(userId, DepartmentAttributeVerdict.Unresolved, current, null, sorted));
                continue;
            }

            var expected = sorted[0];
            var verdict = string.Equals(current, expected, StringComparison.Ordinal)
                ? DepartmentAttributeVerdict.InSync
                : DepartmentAttributeVerdict.Mismatch;
            findings.Add(new(userId, verdict, current, expected, sorted));
        }
        return findings;
    }
}

public enum DepartmentAttributeVerdict
{
    /// <summary>属性がちょうど 1 つの部門グループのコードと一致している。</summary>
    InSync,

    /// <summary>ちょうど 1 つの部門グループに属するのに、属性が違う（または無い）。**属性をグループへ直す対象。**</summary>
    Mismatch,

    /// <summary>部門グループが 0 個または 2 つ以上（同期は所属者から集めるので 0 個の人は通常現れない）。**上書きしない。**</summary>
    Unresolved,
}

/// <summary>1 人分の判定。<see cref="Expected"/> は <see cref="DepartmentAttributeVerdict.Unresolved"/> のとき null。</summary>
public sealed record DepartmentAttributeFinding(
    string UserId,
    DepartmentAttributeVerdict Verdict,
    string? Current,
    string? Expected,
    IReadOnlyList<string> Codes);
