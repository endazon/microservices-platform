namespace DataSourceService.Domain;

// FR-05, UC-04, SC-06, IADR-0468 (#754): 登録した管理者の**部門グループの所属**から部門コードを導く。
//
// ■ 裁定（利用者 2026-09-26。#754 の選択肢 A）
//   部門コードの値域は **Keycloak realm の `department` グループ**（`/department/<コード>`）であり、
//   データソースの部門は**登録した利用者の部門グループの所属**から導く。
//   本クラスは計画の解決順 **② データソースの既定属性** を埋める材料を作るだけで、新しい段を作らない
//   （呼び出し側 `DataSource.Create` が既定属性へ保存する ＝ SC-06 で見えて直せる）。
//
// ■ 🔴 **ちょうど 1 つのときだけ導く。0 個・2 個以上は導かない（null）。**
//   計画 09_datasource-connectors は「誤った写像は誤った部門を作り、裁量制御が意図しない相手に開く。
//   **安全側は『解決しない』である**」と定める。2 部門に属する人の登録を片方へ寄せるのは推測であり、
//   導かなければ従来どおり予約値 `unassigned`（deny 側）へ倒れる。
//
// ■ 🔴 **フルパスで判定する。グループ名では判定しない。**
//   realm の `groups` クレームは `full.path: false`（名前だけ）で、`/department/sales` と `/teams/sales` を
//   区別できない。名前で突き合わせると**誤った部門を作る**。フルパスは `abac-attributes` スコープの
//   `group-paths` マッパー（クレーム `group_paths`）が運ぶ。**クレームが無い realm では何も導かない**（安全側）。
//
// ■ 入れ子は上位の部門コードに畳む
//   `/department/engineering/backend` の所属者は `engineering` の所属者でもある。別の部門として数えると
//   同じ部門の入れ子だけで「2 つ」に見えて導けなくなる。
//
// ■ **フォルダ名からは推定しない**（① フォルダ → 部門の写像は器が計画で未確定。`DataSource` の注記）。
public static class RegistrantDepartment
{
    // realm の部門グループ木の根。値域の正は realm であり、本定数は「どこを見るか」だけを持つ。
    public const string DepartmentGroupRoot = "/department/";

    /// <summary>
    /// 所属グループのフルパスから部門コードを導く。異なる部門コードが**ちょうど 1 つ**のときだけ返し、
    /// それ以外（0 個・2 個以上）は null を返す。照合は序数（大小文字を畳まない）。
    /// </summary>
    public static string? FromGroupPaths(IEnumerable<string?> groupPaths)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in groupPaths)
        {
            if (path is null || !path.StartsWith(DepartmentGroupRoot, StringComparison.Ordinal)) continue;

            var rest = path[DepartmentGroupRoot.Length..];
            var slash = rest.IndexOf('/');
            var code = slash < 0 ? rest : rest[..slash];
            // `/department/` 直下が空（不正なパス）は部門として数えない。
            if (code.Length == 0) continue;
            codes.Add(code);
        }

        return codes.Count == 1 ? codes.First() : null;
    }
}
