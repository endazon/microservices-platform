namespace DataSourceService.Domain;

// NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, [[IADR-0664]] 決定 1 (#458 段 S4): **設定（`Config`）の平文の資格情報**の判定。
//
// 移送（平文 → Vault ＋ 正規の参照）・移送期間の終了の門（起動時の数え）・書き込みの拒否（SC-06 の API）が同じ判定を使う。
// 🔴 **判定を 2 箇所に持たない。** 移送が「平文」と見なさない値を門が平文と数えると、移送を何度流しても門が開かない。
//
// 平文とは、次のすべてを満たす値である。
//   - キーが秘密のキー（`SecretMask.IsSecretKey`。応答のマスクと同じ集合。コネクタが宣言する資格情報のキーを含む）。
//   - 値が空白でない（空白は「値なし」。`CredentialSupplyOf` の `absent` と同じ）。
//   - 値が参照（`vault:` で始まる）を名乗らない（形の正しさは問わない。参照らしいものは参照として扱い、解決できなければ止める）。
//   - 値がマスクそのもの（`***`）でない（保存されない値。書き戻しの往復で既存値を保つ印。`SecretConfigMask.PreserveMasked`）。
public static class PlaintextCredential
{
    public static bool IsPlaintext(string key, string? value) =>
        SecretMask.IsSecretKey(key)
        && !string.IsNullOrWhiteSpace(value)
        && !ConnectorSecretReference.LooksLikeReference(value)
        && value != SecretMask.Placeholder;

    // 設定のうち平文を持つキー（順序はキー名の序数順。ログ・応答にはキー名だけを出し、値を出さない）。
    public static IReadOnlyList<string> KeysOf(IReadOnlyDictionary<string, string>? config) =>
        config is null
            ? []
            : [.. config.Where(kv => IsPlaintext(kv.Key, kv.Value)).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal)];
}
