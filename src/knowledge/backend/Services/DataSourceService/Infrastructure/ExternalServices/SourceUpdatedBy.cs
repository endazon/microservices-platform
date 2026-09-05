using System.Globalization;
using System.Text.Json;

namespace DataSourceService.Infrastructure.ExternalServices;

// FR-05, UC-04, ADR-0036, ADR-0074, #752: ソース側の更新者を読み取り、**由来まで含めて**分類する純関数。
//
// 🔴 **「取れなかった」と「取ったら空だった」を混ぜない。** `SourceItem.UpdatedBy` は `string?` の
// 1 本しか無く、どちらも最終的には null → `DataSource.ResolveOwner(null)` → 予約値 `system` へ落ちる。
// **落ち方が同じでも由来は潰さない** —— 潰すと、運用時に「項目名の設定を間違えている」のか
// 「ソース側が本当に空なのか」を区別できなくなり、**予約値の山を読み違える**（計画
// 09_datasource-connectors は予約値の件数を測定値として読むと定めている）。
//
// 由来は 4 値である。コネクタは 1 回の Discover につき集計を 1 行だけ記録する（アイテムごとに
// 記録すると、正常な `NotCarried` がログを埋める）。
public enum SourceUpdatedByOrigin
{
    // 項目・列がそもそも無い（構成していない／ソースが返さない）。**既定の状態であり異常ではない。**
    NotCarried,

    // 項目・列は在ったが値が空だった（空文字・空白のみ・JSON null・SQL NULL）。**ソース側のデータ不備**である。
    BlankAtSource,

    // 項目・列は在ったが文字列として読めない（JSON のオブジェクト／配列／真偽値、バイト列等）。
    // **構成した項目名が別物を指している**兆候である。
    Unreadable,

    // 値が取れた。**取れたことは `owner` になることを意味しない** —— 写像表に当たらなければ
    // `ResolveOwner` は null を返し、生の識別子は 1 件も `owner` へ入らない。
    Carried,
}

// 読み取り結果（値と由来）。`Value` が非 null なのは `Carried` のときだけである。
public readonly record struct SourceUpdatedByValue(string? Value, SourceUpdatedByOrigin Origin)
{
    public static SourceUpdatedByValue NotCarried { get; } = new(null, SourceUpdatedByOrigin.NotCarried);
}

public static class SourceUpdatedBy
{
    // 汎用 REST 契約（wiki / saas）で更新者を運ぶ JSON 項目名。**構成可能である** ——
    // 実 Wiki / SaaS 製品ごとに名前が違い（`lastModifiedBy` / `author` 等）、実装が 1 つに決め打てない。
    public const string FieldConfigKey = "updatedByField";
    public const string DefaultField = "updatedBy";

    // 業務DB（db）で更新者を運ぶ列の別名。🔴 **opt-in である** —— 無条件に SELECT へ足すと、
    // その別名を持たない既存の管理者クエリが**全件 SQL エラー**になる（＝破壊的変更）。
    public const string ColumnConfigKey = "updatedByColumn";

    // 派生表から取り出すときの別名。列名そのものを使わないのは、管理者が与える列名と
    // 読み出し側を分離するためである。
    public const string ColumnAlias = "updated_by";

    // JSON（`[JsonExtensionData]` が捕えた未知項目）から読む。項目名の突合は完全一致を優先し、
    // 外れたら大小文字を無視して 1 度だけ引き直す（`JsonSerializerDefaults.Web` が宣言済み
    // プロパティに対して行っている突合と揃える）。
    public static SourceUpdatedByValue FromJson(IReadOnlyDictionary<string, JsonElement>? extra, string fieldName)
    {
        if (extra is null || extra.Count == 0 || string.IsNullOrWhiteSpace(fieldName))
            return SourceUpdatedByValue.NotCarried;

        if (!TryGetElement(extra, fieldName, out var element))
            return SourceUpdatedByValue.NotCarried;

        return element.ValueKind switch
        {
            // 項目は在るのに値が null ＝ ソース側が空である（項目が無いのとは違う）。
            JsonValueKind.Null or JsonValueKind.Undefined => new(null, SourceUpdatedByOrigin.BlankAtSource),
            JsonValueKind.String => FromRawText(element.GetString()),
            // 数値・真偽値・オブジェクト・配列は識別子として読めない。**推測で文字列化しない。**
            _ => new(null, SourceUpdatedByOrigin.Unreadable),
        };
    }

    // ADO.NET のリーダーが返した列値から読む。**プロバイダ差で型が揺れる**ため、
    // 識別子として妥当な素の型だけを受ける（バイト列や独自型は `Unreadable`）。
    public static SourceUpdatedByValue FromDbValue(object? value) => value switch
    {
        null or DBNull => new(null, SourceUpdatedByOrigin.BlankAtSource),
        string s => FromRawText(s),
        // 社員番号を整数列で持つソースは珍しくない。**値そのものであって推測ではない**ため受ける。
        char or short or int or long or decimal or Guid =>
            FromRawText(Convert.ToString(value, CultureInfo.InvariantCulture)),
        _ => new(null, SourceUpdatedByOrigin.Unreadable),
    };

    // 空白のみは「取ったら空だった」である。**値は素のまま載せる**（正規化しない）——
    // 突合の正規化は `DataSource.ResolveOwner` 側の責務であり、2 箇所で畳むと規則が割れる。
    private static SourceUpdatedByValue FromRawText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? new(null, SourceUpdatedByOrigin.BlankAtSource)
            : new(text, SourceUpdatedByOrigin.Carried);

    private static bool TryGetElement(
        IReadOnlyDictionary<string, JsonElement> extra, string fieldName, out JsonElement element)
    {
        if (extra.TryGetValue(fieldName, out element))
            return true;

        foreach (var (key, value) in extra)
        {
            if (!string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
                continue;
            element = value;
            return true;
        }

        element = default;
        return false;
    }

    // SQL へ差し込む列名の検証。**通らない値は SQL に載せない**（未設定として扱う）——
    // 管理者が `query` を自由に書ける経路が別に在ることは、識別子を無検査で連結してよい理由にならない。
    public static bool IsSafeSqlIdentifier(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier.Length > 63)
            return false;
        if (identifier[0] != '_' && !char.IsAsciiLetter(identifier[0]))
            return false;

        foreach (var c in identifier)
        {
            if (c != '_' && !char.IsAsciiLetterOrDigit(c))
                return false;
        }

        return true;
    }
}

// 1 回の Discover における由来ごとの件数。**アイテム単位ではなくサイクル単位で記録する。**
public sealed class SourceUpdatedByTally
{
    public int NotCarried { get; private set; }
    public int BlankAtSource { get; private set; }
    public int Unreadable { get; private set; }
    public int Carried { get; private set; }

    public void Add(SourceUpdatedByOrigin origin)
    {
        switch (origin)
        {
            case SourceUpdatedByOrigin.Carried: Carried++; break;
            case SourceUpdatedByOrigin.BlankAtSource: BlankAtSource++; break;
            case SourceUpdatedByOrigin.Unreadable: Unreadable++; break;
            default: NotCarried++; break;
        }
    }

    // 🔴 `NotCarried` は不備ではない（構成していないだけである）。**鳴らすのは残り 2 つだけ。**
    public bool HasAnomaly => BlankAtSource > 0 || Unreadable > 0;
}
