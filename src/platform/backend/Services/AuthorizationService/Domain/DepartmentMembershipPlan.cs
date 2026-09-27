using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Domain;

// FR-05, FR-09, UC-05, SC-17, 計画 ADR-0116 決定 1, ADR-0115 決定 3, [[IADR-0473]] (#1610):
// **SC-17 の部門欄の保存を、部門グループの所属の変更として計画する**純関数（IdP を呼ばない）。
//
// ■ 裁定（計画 ADR-0116 決定 1）
//   部門欄の選択肢は realm の部門グループのコード（`/department/<code>`）。保存すると、選んだ部門グループへ入れ、
//   ほかの部門グループから外す。部門は 1 つである。「部門なし」はすべての部門グループから外す。
//   利用者属性 `department` は同期（IADR-0473）が追いつく。**SC-17 は属性を直接書かない。**
//
// ■ 🔴 **2 個以上の部門グループに属する人は変えない**（`MultipleDepartments`）。
//   ADR-0116 は 2 個以上の人を扱わない（決定 2・フォローアップ 4）。SC-17 で 1 つを選ばせて残りを外すのは、
//   管理者が意図して組んだかもしれない複数所属を黙って崩すことになる。Keycloak で所属を 1 つにしてから変える。
//
// ■ 入れ子は上位のコードに畳む（`DepartmentAttributeReconciliation.CodeOf`。同期と同じ規則）。
//   `/department/engineering/backend` だけに属する人のコードは `engineering` であり、`engineering` を選んでも何もしない
//   （入れ子の所属を崩さない）。別の部門へ移すときは、部門の木の直接の所属をすべて外す。
public static class DepartmentMembershipPlan
{
    /// <summary>
    /// 計画を立てる。<paramref name="currentGroups"/> は利用者の直接の所属（部門の木の外を含んでよい。外は触らない）、
    /// <paramref name="targetCode"/> は選んだ部門コード（null ＝ 部門なし）、<paramref name="targetGroupId"/> はそのグループの ID
    /// （部門なしのときは null）。
    /// </summary>
    public static DepartmentMembershipChange Plan(
        IReadOnlyList<IdentityGroup> currentGroups, string? targetCode, string? targetGroupId)
    {
        if (targetCode is not null && string.IsNullOrEmpty(targetGroupId))
            throw new ArgumentException("部門コードを選んだときは、そのグループの ID が要る。", nameof(targetGroupId));

        var departmentGroups = currentGroups
            .Where(g => DepartmentAttributeReconciliation.CodeOf(g.Path) is not null)
            .OrderBy(g => g.Path, StringComparer.Ordinal)
            .ToList();
        var codes = CodesOf(departmentGroups);

        if (codes.Count > 1) return new(DepartmentMembershipVerdict.MultipleDepartments, codes, null, []);

        var alreadyThere = targetCode is null
            ? codes.Count == 0
            : codes.Count == 1 && string.Equals(codes[0], targetCode, StringComparison.Ordinal);
        if (alreadyThere) return new(DepartmentMembershipVerdict.Unchanged, codes, null, []);

        // 🔴 **外すのは部門の木の直接の所属だけ**（`/teams/*` 等には触れない）。目的のグループそのものは外さない。
        var leave = departmentGroups
            .Where(g => !string.Equals(g.Id, targetGroupId, StringComparison.Ordinal))
            .Select(g => g.Id)
            .ToList();
        return new(DepartmentMembershipVerdict.Move, codes, targetGroupId, leave);
    }

    /// <summary>所属から部門コードの集合を取り出す（序数順・重複なし）。部門の木の外は落ちる。</summary>
    public static IReadOnlyList<string> CodesOf(IEnumerable<IdentityGroup> groups)
        =>
        [
            .. groups
                .Select(g => DepartmentAttributeReconciliation.CodeOf(g.Path))
                .Where(code => code is not null)
                .Select(code => code!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(code => code, StringComparer.Ordinal)
        ];
}

public enum DepartmentMembershipVerdict
{
    /// <summary>すでに目的の状態（コードが目的の 1 つ、または部門なしで 0 個）。何も書かない。</summary>
    Unchanged,

    /// <summary>目的のグループへ入れ（部門なしなら入れない）、ほかの部門グループから外す。</summary>
    Move,

    /// <summary>🔴 部門グループが 2 個以上。**SC-17 からは変えない**（計画 ADR-0116 の対象外）。</summary>
    MultipleDepartments,
}

/// <summary>
/// 1 人分の計画。<see cref="JoinGroupId"/> は入れるグループ（部門なし・変えないときは null）、
/// <see cref="LeaveGroupIds"/> は外すグループ。<see cref="CurrentCodes"/> は計画時の部門コード（序数順）。
/// </summary>
public sealed record DepartmentMembershipChange(
    DepartmentMembershipVerdict Verdict,
    IReadOnlyList<string> CurrentCodes,
    string? JoinGroupId,
    IReadOnlyList<string> LeaveGroupIds);
