namespace DataSourceService.Domain;

// FR-05, UC-04, SC-06, ADR-0036, ADR-0064, ADR-0074 (#1194): `owner` の写像表の正規化と検証。
//
// 計画 ADR-0074 決定 4 は「**写像先の利用者識別子は、登録時に実在を検証する。検証に通らない対は
// 保存しない**」と定める。ここに置くのは**純粋な判断だけ**であり、名簿の取得（HTTP）は
// `IPlatformUserDirectory` の実装が担う（ADR-0065 決定 2 の層分け。判断は Domain、境界は Infrastructure）。
//
// 🔴 **「実在しない」と「確かめられなかった」を混ぜない。** 名簿を引けなかったときに
// 「存在しない」と報告するのは嘘であり、運用者は実在する利用者を消したと読む。
// **どちらも保存しないので安全側は同じ**だが、返す理由と HTTP ステータスは分ける。
public static class OwnerMappingTable
{
    // 前後空白だけを落とす。**それ以外の正規化はしない**（大小文字を畳むと、別名前空間の
    // 識別子どうしを実装の裁量で同一視することになる。09_datasource-connectors「推測で埋めない」）。
    //
    // **空キー・空値の対はここで捨てない。** 捨てると「入れたのに効かない」になるため、
    // `Validate` が 400 で拒否できるよう素通しする（判断は 1 箇所に置く）。
    public static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? mappings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (mappings is null) return result;

        foreach (var (key, value) in mappings)
        {
            var trimmedKey = key?.Trim() ?? string.Empty;
            if (trimmedKey.Length == 0) continue;
            result[trimmedKey] = value?.Trim() ?? string.Empty;
        }

        return result;
    }

    // 書式の検査。**名簿を引く前に済ませられる分**をここで済ませる（後段に問い合わせるまでもない）。
    public static List<string> ValidateShape(IReadOnlyDictionary<string, string>? mappings)
    {
        var errors = new List<string>();
        if (mappings is null || mappings.Count == 0) return errors;

        foreach (var (key, value) in mappings)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                errors.Add("ソース側の利用者識別子が空の対があります。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"写像先の利用者識別子が空です（ソース側: {key.Trim()}）。");
        }

        return errors;
    }

    // 実在の検査。**名簿に無い写像先を列挙して返す。**
    //
    // 理由（どの値が実在しないか）を返してよい根拠: SC-06 の登録・更新は**管理者限定**であり
    // （ADR-0074 決定 1・計画 §SC-06）、その管理者は SC-17 で利用者一覧を丸ごと閲覧できる。
    // **伏せても隠せる情報が無い。** ADR-0074 決定 4 が課しているのは「保存しない」ことだけで、
    // 存在秘匿は課していない。
    public static List<string> ValidateTargetsExist(
        IReadOnlyDictionary<string, string> mappings, IReadOnlySet<string> knownUsernames)
    {
        var missing = mappings.Values
            .Select(v => v.Trim())
            .Where(v => v.Length > 0 && !knownUsernames.Contains(v))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        return missing.Count == 0
            ? []
            : [$"写像先の利用者が存在しません: {string.Join(", ", missing)}。"
               + "利用者識別子（ログイン名）で指定してください。"];
    }
}
