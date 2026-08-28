namespace DataSourceService.Domain;

// FR-01, FR-05, SC-06, IADR-0053 / IADR-0148 決定 6 / IADR-0295 決定 1:
// コネクタ設定（Config）の秘密の扱い。
//
// **読み（マスク）と書き（マスク値を保存しない）で同じ知識を使う。** どちらが「秘密キーか」を
// 2 箇所に持つと、片方にマーカーを足したときにもう片方が黙って古くなる —— 本リポジトリが
// 繰り返し踏んでいる型である。
//
// ［2026-08-28 追記 / #458］**そのとおりのことが実際に起きていた。** 本クラスの 4 語と
// `SyncErrorRedactor` の 7 種が食い違い、`apiKey` / `pwd` / `privateKey` が応答で伏せられて
// いなかった。**マーカーは `SecretMask.KeyMarkers` へ統合した**（IADR-0295 決定 1）。
// 本クラスは辞書に対する適用（Redact / PreserveMasked）だけを持ち、**何が秘密かは持たない。**
//
// 守っている往復（IADR-0148 決定 6）:
//   GET → 応答の apiToken は "***"（Redact）→ 利用者がそれを編集して PUT/PATCH で送り返す
//   → **"***" は保存せず既存値を保つ**（PreserveMasked）。
// これが無いと、読んで書き戻すという API の最も普通の使い方が資格情報を破壊する。
public static class SecretConfigMask
{
    // マスク後の値。実体は SecretMask が持つ（同じ文字列を 2 箇所に置かない）。
    public const string Placeholder = SecretMask.Placeholder;

    // 秘密とみなすキーかどうかの判定は SecretMask ただ 1 本に委ねる。
    public static bool IsSecretKey(string key) => SecretMask.IsSecretKey(key);

    // 応答用に秘密の値を伏せる（IADR-0053。Vault 移行までの暫定措置）。
    public static Dictionary<string, string> Redact(IReadOnlyDictionary<string, string> config)
        => config.ToDictionary(kv => kv.Key, kv => IsSecretKey(kv.Key) ? Placeholder : kv.Value);

    // 書き込み時の防御: **秘密キーの値がマスクそのものなら保存しない。**
    //   - 既存値があれば**それを保つ**（読んで書き戻す往復が無害になる）
    //   - 既存値が無ければ**そのキーを落とす**（"***" という資格情報は存在しない。保存すると
    //     接続時に意味の分からない失敗になる）
    // **秘密キーでなければ "***" は普通の値**である —— マスクされて返らないので、利用者が意図して
    // 入れた文字列と区別できる。過剰に守らない。
    public static Dictionary<string, string> PreserveMasked(
        IReadOnlyDictionary<string, string> incoming,
        IReadOnlyDictionary<string, string> existing)
    {
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in incoming)
        {
            if (IsSecretKey(key) && value == Placeholder)
            {
                if (existing.TryGetValue(key, out var kept)) result[key] = kept;
                continue;
            }
            result[key] = value;
        }
        return result;
    }
}
