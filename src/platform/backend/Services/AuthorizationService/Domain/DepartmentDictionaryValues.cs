namespace AuthorizationService.Domain;

// FR-05, FR-09, UC-05, SC-09, SC-17, 計画 ADR-0116 決定 3, ADR-0115 決定 1, [[IADR-0476]] (#1609):
// **属性辞書の `department` の許可値は、realm の部門グループ（`/department/<code>`）から導く**（純関数。IdP を呼ばない）。
//
// ■ 裁定（計画 ADR-0116 決定 3）
//   SC-09 の属性辞書は `department` の値の集合を手で持たない。realm の部門グループのコードを値の集合とする（ADR-0115 決定 1 の
//   値域と同じ）。ポリシーの許容値と SC-17 の選択肢はこの集合から選ぶ。部門を足す・消すときは realm の部門グループを変える。
//   従前は seed の固定値（`finance` / `legal` を含む）と realm（`engineering` / `sales` / `hr`）が食い違っていた。
//
// ■ 🔴 **realm を読めないときは「不明」であり、「部門が無い」ではない**（原則 A）。
//   保存済みの値（最後に realm から確かめた値）をそのまま使い、**消さない**。画面には出所を「不明」として示す。
//   根（`/department`）が無い realm も不明に倒す（部門の体系が組まれていない realm で辞書を空にしない）。
//   根はあるが子が 0 個なら「部門が無い」という確定した答えである。
//
// ■ 対象は利用者・文書の両スコープの `department`（キーは大小文字無視。辞書のキーの一意性と同じ照合）。
public static class DepartmentDictionaryValues
{
    public const string Key = "department";

    /// <summary>応答の <c>allowedValuesSource</c>: realm の部門グループから導いた（今回読めた）。</summary>
    public const string SourceRealm = "realm";

    /// <summary>応答の <c>allowedValuesSource</c>: realm を読めず、保存済みの値（最後に確かめた値）を示している ＝ 不明。</summary>
    public const string SourceRealmUnavailable = "realm-unavailable";

    /// <summary>許可値を realm から導くキーか（大小文字無視）。</summary>
    public static bool IsDerived(string? key) => string.Equals(key, Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>応答に載せる値の出所。手で持つキーは null。</summary>
    public static string? SourceOf(string? key, DepartmentDomainReading reading)
        => !IsDerived(key) ? null : reading.Known ? SourceRealm : SourceRealmUnavailable;

    /// <summary>
    /// 導くキーの実効の許可値。realm を読めたら realm のコード、読めなければ保存済みの値（無ければ空）。
    /// 🔴 **読めないときに空へ倒さない**（既存の値を消さない）。
    /// </summary>
    public static IReadOnlyList<string> Effective(IReadOnlyList<string>? stored, DepartmentDomainReading reading)
        => reading.Known ? reading.Codes : stored ?? [];

    /// <summary>
    /// 登録・更新の要求の許可値を受け付けてよいか。**空（＝ realm から導く）か、実効の値と同じ集合**のときだけ受け付ける
    /// （序数・並びは問わない）。手で足す・消す要求は拒む —— 部門の追加・削除は realm の部門グループで行う。
    /// </summary>
    public static bool RequestAccepted(IReadOnlyList<string>? requested, IReadOnlyList<string> effective)
        => requested is null || requested.Count == 0
           || new HashSet<string>(requested, StringComparer.Ordinal).SetEquals(effective);

    /// <summary>拒んだときの理由（画面へそのまま出る）。</summary>
    public static string RejectionMessage(DepartmentDomainReading reading)
        => reading.Known
            ? $"属性 '{Key}' の許可値は realm の部門グループから導かれるため、手で足す・消すことはできません"
              + $"（現在の部門グループ: {(reading.Codes.Count == 0 ? "なし" : string.Join(", ", reading.Codes))}）。"
              + "部門の追加・削除は realm の部門グループで行ってください。許可値を空にして送ると realm の値が入ります。"
            : $"属性 '{Key}' の許可値は realm の部門グループから導かれますが、いま realm を読めないため確かめられません。"
              + "許可値を空にするか、保存済みの値のまま送ってください。";
}

/// <summary>
/// realm の部門グループの読み取り結果。<see cref="Known"/> が false なら <see cref="Codes"/> は空で意味を持たない（不明）。
/// <see cref="Codes"/> は序数順・重複なし。
/// </summary>
public sealed record DepartmentDomainReading(bool Known, IReadOnlyList<string> Codes)
{
    public static DepartmentDomainReading Unknown { get; } = new(false, []);

    public static DepartmentDomainReading Of(IEnumerable<string> codes)
        => new(true, [.. codes.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal)]);
}
