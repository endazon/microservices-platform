namespace McpServer.Domain.Ports;

// FR-16, FR-05, UC-09, SC-12, ADR-0062 決定 3: 登録操作を行った利用者（登録者）が
// 無人アカウントへ**渡してよい**属性値の集合を解決する読み口。
//
// ■ なぜ後段が解決するのか
//   ADR-0062 決定 3:「判定は後段（MCP クライアント登録を受けるサービス）が行う。画面・BFF は
//   値域の絞り込み（定義済みの値のみ）までを担い、部分集合の判定は行わない。」
//   🔴 **画面が判定しても後段が同じ判定を持たなければ API を直接叩けば素通しになる。**
//   決定 4 が身元の口（`/bff/auth/me`）へ `clearance` / `department` を足さないと定めているのは、
//   属性の正が認可サービスひとつであることを契約の側で保つためである。
//
// ■ 🔴 **「配れない」と「引けなかった」を型で分ける**
//   空集合を返して済ませると、認可サービスが落ちている間の拒否がすべて
//   「あなたはその区分を持っていません」になる。**どちらも配らない点では安全側だが、報告は嘘になる。**
//   （DataSourceService の `IPlatformUserDirectory` と同じ理由・同じ形。）
public interface IRegistrarAttributeResolver
{
    /// <summary>
    /// 現在の要求の呼び出し元（登録者）が無人アカウントへ渡してよい属性値の集合を解決する。
    /// 解決できなければ <see cref="RegistrarAssignableAttributes.Available"/> が false。
    /// </summary>
    Task<RegistrarAssignableAttributes> ResolveAsync(CancellationToken ct);
}

// FR-16, SC-12, ADR-0062 決定 2: 登録者が渡してよい属性値の集合。
//
// **`Available=false` は「1 つも持っていない」ではない**（上の注記）。
//
// <see cref="ClearanceUnrestricted"/> は「登録者の読み取りが機密区分で絞られていない」ことを表す。
// 認可スコープ契約は「`Granted=true` かつフィルタが空 ＝ 条件無しで許可（全件可）」と定めており
// （`AccessScopeResponse`）、その場合に `clearance` を絞る根拠は無い。
public sealed record RegistrarAssignableAttributes(
    bool Available,
    bool ClearanceUnrestricted,
    IReadOnlySet<string> Clearance,
    IReadOnlySet<string> Tags)
{
    /// <summary>登録者の属性を引けなかった（＝判定できない）。**何も配らない。**</summary>
    public static RegistrarAssignableAttributes Unavailable { get; } = new(
        false, false,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public static RegistrarAssignableAttributes Of(
        IEnumerable<string> clearance, IEnumerable<string> tags, bool clearanceUnrestricted = false)
        => new(
            true, clearanceUnrestricted,
            new HashSet<string>(clearance, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase));
}
