namespace AuthorizationService.Domain;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0115 決定 3, ADR-0116 決定 2, [[IADR-0473]] (#1573, #1609):
// **利用者属性 `department` を部門グループの所属に合わせる計画**を立てる純関数（IdP を呼ばない）。
//
// ■ 裁定（計画 ADR-0115 決定 3）
//   利用者の部門の正本は部門グループ（`/department/<code>`）への所属であり、ABAC が読む利用者属性 `department` は
//   それと一致させる。食い違いを見つけたら**属性をグループに合わせて直す。逆向きには直さない。**
//
// ■ 🔴 **ちょうど 1 つの部門に属するときだけ直す。2 個以上は未解決として上書きしない。**
//   2 部門に属する人の属性を片方へ寄せるのは推測である（データソースの既定部門を導く `RegistrantDepartment` と同じ規則。
//   計画 09_datasource-connectors「安全側は解決しない」）。**2 個以上の人の属性は消しもしない**（ADR-0116 決定 2 の対象外）。
//
// ■ ［2026-09-27 / #1609・計画 ADR-0116 決定 2］🔴 **部門グループに 1 つも属さない人の属性は消す**（`Orphaned`）。
//   グループが正本であり、所属が無ければ部門も無い。従前（#1573）はこの人を未解決として残していた —— 異動や退職で
//   グループから外した人が、前の部門の資料を見続けていた。
//   **ただし「0 個」は全利用者の列挙を最後まで読めたときにしか言えない**（原則 A: 読めなかった人は「0 個」ではなく「不明」）。
//   その判断は呼び出し元（`DepartmentAttributeSync`）が持つ —— 本関数は渡された人を渡されたとおりに判定する。
//   **サービスアカウント（利用者名が `service-account-` で始まる）は呼び出し元が渡さない**（`IsServiceAccount`）。
//   部門グループに属さない機械の主体の属性は所属から何も言えない（開発用 realm の `service-account-abac-seeder` は
//   部門グループなしで `department` を持つ）。
//
// ■ 入れ子は上位のコードに畳む（`/department/engineering/backend` の所属者は `engineering`）。同じ部門の入れ子だけで
//   「2 つ」に見えて直せなくなるのを避ける。**照合は序数**（大小文字を区別。Keycloak のグループ名と同じ）。
public static class DepartmentAttributeReconciliation
{
    public const string AttributeKey = "department";
    public const string DepartmentGroupRoot = "/department/";

    // Keycloak が client credentials の主体へ付ける利用者名の接頭辞（`MachinePrincipal.ServiceAccountUsernamePrefix` と同じ値。
    // Domain から共通基盤の観測の型を引かないため、ここにも置く）。
    public const string ServiceAccountUsernamePrefix = "service-account-";

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

    /// <summary>利用者名が Keycloak のサービスアカウントの形か（大小文字無視）。</summary>
    public static bool IsServiceAccount(string? username)
        => username is not null && username.StartsWith(ServiceAccountUsernamePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 利用者ごとの判定を返す。<paramref name="codesByUser"/> は利用者（IdP の内部 ID）→ 所属する部門コードの集合
    /// （**空集合は「部門グループに 1 つも属さない」**。呼び出し元は全利用者の列挙を読み切れたときだけ空集合の人を入れる）、
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

            if (sorted.Length == 0)
            {
                // #1609: 所属 0 個。属性が残っていれば消す対象、無ければ一致している（部門なし）。
                findings.Add(new(userId,
                    current is null ? DepartmentAttributeVerdict.InSync : DepartmentAttributeVerdict.Orphaned,
                    current, null, sorted));
                continue;
            }

            if (sorted.Length > 1)
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
    /// <summary>属性がちょうど 1 つの部門グループのコードと一致している（または部門グループ 0 個で属性も無い）。</summary>
    InSync,

    /// <summary>ちょうど 1 つの部門グループに属するのに、属性が違う（または無い）。**属性をグループへ直す対象。**</summary>
    Mismatch,

    /// <summary>部門グループが 2 つ以上。**上書きしない・消さない。**</summary>
    Unresolved,

    /// <summary>
    /// ［#1609・計画 ADR-0116 決定 2］部門グループに 1 つも属さないのに属性 `department` が残っている。**属性を消す対象。**
    /// </summary>
    Orphaned,
}

/// <summary>
/// 1 人分の判定。<see cref="Expected"/> は 1 つ属する人（食い違い・一致）のときだけ値を持ち、
/// 未解決（2 個以上）・消す対象（0 個）・部門なしの一致では null。
/// </summary>
public sealed record DepartmentAttributeFinding(
    string UserId,
    DepartmentAttributeVerdict Verdict,
    string? Current,
    string? Expected,
    IReadOnlyList<string> Codes);
