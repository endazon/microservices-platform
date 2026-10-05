namespace DataSourceService.Domain;

// FR-01, NFR-18, [[IADR-0493]] 決定 2 (#458 段 S0): `Config` に保存する Vault 参照の形 `vault:<path>#<key>`。
//
// **パスの置き場所（接頭辞）はここで決めない。** 本型が守るのは形だけである。接頭辞（ESO の `msp/*` policy の外の
// `datasource/`）は段 S1 が配備の policy と一緒に決め、Vault 解決器が守る（[[IADR-0495]] 決定 1。
// `VaultConnectorSecretResolver.IsUnderDedicatedPrefix`）。
//
// ■ 🔴 **接頭辞の判定は大文字小文字を区別しない**
//   `VAULT:…` を「参照ではない」と判定すると、移送期間の解決器はそれを**平文として外部へ送る**。
//   参照らしいものは参照として扱い、解決できなければ止める側へ倒す（fail-closed）。
public sealed record ConnectorSecretReference(string Path, string Key)
{
    public const string Scheme = "vault:";

    /// <summary>
    /// コネクタの資格情報を置く専用接頭辞（KV マウントからの相対）。[[IADR-0495]] 決定 1 の読み取りの policy、
    /// [[IADR-0501]] 決定 2 の BFF の書き込みの policy（`datasource/+`）と対で固定する。
    /// </summary>
    public const string DedicatedPathPrefix = "datasource/";

    // SC-22, 計画 ADR-0126 決定 1, [[IADR-0501]] 決定 3 (#458 段 S2): データソース 1 件の資格情報の項目の Vault のパス。
    // 🔴 **ID は `D` 形式（小文字・ハイフン区切り）に固定する。** BFF が書くパス（`datasource/<ID>`）と 1 文字でも違えば、
    // 参照は別の場所を指し、画面から書いた値が使われない。
    public static string PathFor(Guid dataSourceId) => DedicatedPathPrefix + dataSourceId.ToString("D");

    // 計画 ADR-0126 決定 4, [[IADR-0501]] 決定 3: 画面から書いた値を指す**正規の参照**（`vault:datasource/<ID>#<キー>`）。
    public static string CanonicalFor(Guid dataSourceId, string key) => $"{Scheme}{PathFor(dataSourceId)}#{key}";

    // 値が参照の形を名乗っているか（形が正しいかは問わない）。
    public static bool LooksLikeReference(string? value)
        => value is not null
            && value.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    // `vault:<path>#<key>` を読む。`#` はちょうど 1 つ、パスとキーはともに空でなく空白を含まない。
    public static bool TryParse(string? value, out ConnectorSecretReference? reference)
    {
        reference = null;
        if (!LooksLikeReference(value))
            return false;

        var body = value!.Trim()[Scheme.Length..];
        var hash = body.IndexOf('#');
        if (hash <= 0 || hash != body.LastIndexOf('#') || hash == body.Length - 1)
            return false;

        var path = body[..hash];
        var key = body[(hash + 1)..];
        if (path.Any(char.IsWhiteSpace) || key.Any(char.IsWhiteSpace))
            return false;

        reference = new ConnectorSecretReference(path, key);
        return true;
    }

    // 🔴 **パスを出さない。** 値ではないが Vault の構造を漏らす（作業仕様書 §未決事項 3）。
    // 切り分けに要るのはキー名までである。
    public override string ToString() => $"{Scheme}{SecretMask.Placeholder}#{Key}";
}
