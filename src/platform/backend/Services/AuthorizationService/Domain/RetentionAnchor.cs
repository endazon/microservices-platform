using System.Globalization;

namespace AuthorizationService.Domain;

// FR-19, SC-17, SC-19, 計画 ADR-0036 D-09, ADR-0082 決定 5・フォローアップ 3, [[IADR-0428]] (#1392):
// **退職時に個人資料へ掛かる 30 日の窓の「起点」**（retention anchor）。
//
// ■ なぜ起点を明示的に持つのか
//   `ADR-0036` D-09 は管理者閲覧権の有効期限を「**退職日**から 30 日間」と定めるが、
//   🔴 **退職日を取得する経路が存在しない** —— 人事システム連携は未実装であり、
//   Keycloak の無効化フラグ（`enabled`）に**日付は無い**。`ADR-0082` 決定 5 が
//   「暫定期間中は **SC-17 でアカウントが無効化された日**を起点とする」と部分改定し、
//   フォローアップ 3 が「**配備後の切り替えを見越し、起点を構成または属性で持つ形にしておくこと**」を
//   実装側へ降ろした。本ファイルはその「構成 ＋ 属性」である。
//
// ■ 🔴 期間（30 日）は構成にしない
//   `ADR-0082` 決定 5 は「**期間（30 日）は変えない。変えるのは起点だけである**」と明記する。
//   構成にすると、配備が計画の決定を黙って弱められる。**構成にするのは起点の出所だけ**である。
//
// ■ 🔴 `PrivateNote.RetentionDays = 90` とは別の規則である
//   あちら（knowledge ユニット）は `ADR-0037` 決定 5・16 の「論理削除の保管期間／版履歴の日数条件」で
//   あり、**退職とは無関係**である。同じ「保持」という語だが起点も対象も違う ——
//   1 つの定数を共有させると、片方を直したときにもう片方が黙って変わる。

/// <summary>
/// 起点の出所。**構成 <c>RetentionAnchor:Source</c> で選ぶ**（`ADR-0082` フォローアップ 3）。
/// </summary>
public enum RetentionAnchorSource
{
    /// <summary>暫定（現行）: SC-17 でアカウントが無効化された日（`ADR-0082` 決定 5）。</summary>
    AccountDisabledAt,

    /// <summary>
    /// 恒久: 人事システム由来の退職日（`ADR-0036` D-09 の原文）。
    /// 🔴 **書き手（人事連携）は未実装である。** 選んでも起点は未供給のままであり、
    /// 判定は <see cref="RetentionEligibility.NotEvaluable"/> へ倒れる（＝削除しない）。
    /// </summary>
    HrLeaveDate,
}

// FR-19, SC-17, ADR-0082 フォローアップ 3, [[IADR-0428]] (#1392): 起点の出所の宣言。
//
// 🔴 **値域外は起動時に落とす**（`IdentityAdmin:Provider` と同じ deny-by-default）。
// 綴りを間違えた配備が「既定の起点で動いている」へ倒れると、**どの日付を起点に窓を数えているのかを
// 誰も答えられなくなる。**
public sealed class RetentionAnchorOptions
{
    public const string SourceKey = "RetentionAnchor:Source";
    public const string AccountDisabledAtValue = "account-disabled-at";
    public const string HrLeaveDateValue = "hr-leave-date";

    /// <summary>既定は暫定側（`ADR-0082` 決定 5）。**未宣言の配備は暫定側で正しい。**</summary>
    public RetentionAnchorSource Source { get; init; } = RetentionAnchorSource.AccountDisabledAt;

    /// <summary>選んだ出所が載る IdP 利用者属性のキー。</summary>
    public string AttributeKey => Source == RetentionAnchorSource.HrLeaveDate
        ? RetentionAnchorAttributes.HrLeaveDateKey
        : RetentionAnchorAttributes.AccountDisabledAtKey;

    public static RetentionAnchorOptions FromConfiguration(IConfiguration configuration)
    {
        var declared = configuration[SourceKey];
        if (string.IsNullOrWhiteSpace(declared))
            return new RetentionAnchorOptions();

        return declared.Trim() switch
        {
            AccountDisabledAtValue => new RetentionAnchorOptions
            {
                Source = RetentionAnchorSource.AccountDisabledAt,
            },
            HrLeaveDateValue => new RetentionAnchorOptions
            {
                Source = RetentionAnchorSource.HrLeaveDate,
            },
            _ => throw new InvalidOperationException(
                $"{SourceKey} の値 '{declared}' は不正である"
                + $"（'{AccountDisabledAtValue}' / '{HrLeaveDateValue}' のいずれか）。"
                + " 未設定なら暫定側（アカウント無効化日）を既定とする。"),
        };
    }
}

// FR-19, SC-17, [[IADR-0428]] (#1392): 起点を載せる IdP 利用者属性のキー（**予約キー**）。
//
// 🔴 **予約キーは ABAC 属性ではない。** 3 点で分ける。
//   1. **SC-17 の応答 DTO へ出さない**（`PlatformUserMapper`）。出すと画面の権限編集ダイアログが
//      下書き（`{...editing.attributes}`）へ写して送り返し、`UserAssignmentValidation.
//      ValidateAttributes` が「辞書に無いキー」として **400** を返す ——
//      **無効化済み利用者の属性編集が壊れる。**
//   2. **`ReplaceAttributesAsync`（ABAC 属性の差し替え）では消えない。** 差し替えは ABAC 属性だけを
//      置き換え、予約キーは現在値を持ち越す。持ち越さないと、部門を 1 つ直しただけで起点が黙って消える
//      （消えても削除はされない＝安全側だが、**窓をいつまでも計算できない**）。
//   3. **書き手は `IIdentityAdminClient.SetRetentionAnchorAsync` ただ 1 つ**である。
//      差し替えの口と兼用にすると、2 の「保存する」と再有効化の「消す」が同じ口で表せない。
public static class RetentionAnchorAttributes
{
    /// <summary>暫定の起点（`ADR-0082` 決定 5）。**書き手は SC-17 の無効化端点**である。</summary>
    public const string AccountDisabledAtKey = "account_disabled_at";

    /// <summary>
    /// 恒久の起点（`ADR-0036` D-09 の原文）。**書き手は人事連携であり、未実装である**
    /// （`ADR-0082` 決定 1 の方式選定は、人事システムの実体が計画へ入るまで着手できない）。
    /// </summary>
    public const string HrLeaveDateKey = "hr_leave_date";

    /// <summary>予約キー（ABAC 属性の値域とは別系統。上の 🔴 を参照）。</summary>
    public static IReadOnlySet<string> ReservedKeys { get; } =
        new HashSet<string>(StringComparer.Ordinal) { AccountDisabledAtKey, HrLeaveDateKey };

    public static bool IsReserved(string key) => ReservedKeys.Contains(key);

    /// <summary>予約キーを落とした像（SC-17 の応答 DTO・画面へ渡す側）。</summary>
    public static IReadOnlyDictionary<string, string> WithoutReserved(
        IReadOnlyDictionary<string, string> attributes)
        => attributes.Where(kv => !IsReserved(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>
    /// ABAC 属性の差し替えで**予約キーだけを持ち越す**（上の 🔴 2）。
    /// 要求側に予約キーが混ざっていても採らない —— 書き手は 1 つだけである。
    /// </summary>
    public static IReadOnlyDictionary<string, string> PreserveReserved(
        IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> requested)
    {
        var merged = requested.Where(kv => !IsReserved(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var (key, value) in current.Where(kv => IsReserved(kv.Key)))
            merged[key] = value;
        return merged;
    }
}

/// <summary>起点の解決結果。**「未供給」と「読めない」を分ける**（理由が違えば直し方も違う）。</summary>
public enum RetentionAnchorState
{
    /// <summary>起点が載っている。</summary>
    Supplied,

    /// <summary>属性が無い・空である。**人事連携が未配備なら通常はこれ**である。</summary>
    NotSupplied,

    /// <summary>属性はあるが日付として読めない（`"0"` 等）。**エポックへ倒さない。**</summary>
    Unreadable,
}

/// <summary>解決した起点。<see cref="At"/> は <see cref="RetentionAnchorState.Supplied"/> のときだけ非 null。</summary>
public readonly record struct RetentionAnchor(
    RetentionAnchorState State, DateTimeOffset? At, string? RawValue)
{
    public static RetentionAnchor NotSupplied { get; } = new(RetentionAnchorState.NotSupplied, null, null);
}

/// <summary>
/// 窓の判定。🔴 **bool にしない** —— `false` にすると「起点が無い」が「まだ経っていない」に化け、
/// 後から「本当に経過したのか、そもそも数えていないのか」を誰も答えられなくなる
/// （「**0 件**」と「**未供給**」を分ける規約。#1392）。
/// </summary>
public enum RetentionEligibility
{
    /// <summary>起点から 30 日が経過した。</summary>
    Elapsed,

    /// <summary>起点はあるが 30 日はまだ経っていない。</summary>
    WithinWindow,

    /// <summary>🔴 **起点が無い／読めない。数えていない。** 削除の対象にしてはならない（fail-safe）。</summary>
    NotEvaluable,
}

// FR-19, SC-19, 計画 ADR-0036 D-09, ADR-0082 決定 5, [[IADR-0428]] (#1392): 起点の解決と窓の判定。
//
// 🔴 **純関数である。** 器（HTTP・IdP・DB）を起こさずに境界を試験できるようにするため
// （`UserAssignmentValidation` と同じ理由。IADR-0129 決定 6）。
public static class RetentionAnchorPolicy
{
    /// <summary>
    /// 計画 `ADR-0036` D-09 の窓（**30 日**）。`ADR-0082` 決定 5 は起点だけを改定し、
    /// **期間は改めていない**。構成で動かさない（本ファイル冒頭の 🔴）。
    /// </summary>
    public const int RetentionDays = 30;

    /// <summary>
    /// 起点を書くときの書式（ISO-8601・UTC・秒精度）。読み側の
    /// <see cref="AcceptedFormats"/> の先頭と同じものである。
    /// </summary>
    public const string WireFormat = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>
    /// 🔴 **固定書式でしか読まない。** 一般の <c>TryParse</c> は文化圏と入力しだいで
    /// `"0"` のような値まで日付として通し得る。**「0」は起点ではない。**
    /// 人事由来の値は日付だけ（`yyyy-MM-dd`）で来ることを見込んで受ける。
    /// </summary>
    public static IReadOnlyList<string> AcceptedFormats { get; } =
        ["yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-dd"];

    /// <summary>IdP の利用者属性から、構成が選んだ出所の起点を解決する。</summary>
    public static RetentionAnchor Resolve(
        IReadOnlyDictionary<string, string> attributes, RetentionAnchorOptions options)
    {
        if (!attributes.TryGetValue(options.AttributeKey, out var raw) || string.IsNullOrWhiteSpace(raw))
            return RetentionAnchor.NotSupplied;

        return DateTimeOffset.TryParseExact(
            raw.Trim(), [.. AcceptedFormats], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? new RetentionAnchor(RetentionAnchorState.Supplied, at, raw)
            : new RetentionAnchor(RetentionAnchorState.Unreadable, null, raw);
    }

    /// <summary>
    /// 窓を判定する。🔴 **起点が無ければ数えない**（<see cref="RetentionEligibility.NotEvaluable"/>）——
    /// 「削除しない」が既定であり、起点の不在を「経過した」へ倒す経路は存在しない。
    /// </summary>
    public static RetentionEligibility Evaluate(RetentionAnchor anchor, DateTimeOffset now)
        => anchor is { State: RetentionAnchorState.Supplied, At: { } at }
            ? now >= at.AddDays(RetentionDays) ? RetentionEligibility.Elapsed : RetentionEligibility.WithinWindow
            : RetentionEligibility.NotEvaluable;

    /// <summary>起点を属性へ載せるときの文字列。</summary>
    public static string Format(DateTimeOffset at)
        => at.ToUniversalTime().ToString(WireFormat, CultureInfo.InvariantCulture);
}
