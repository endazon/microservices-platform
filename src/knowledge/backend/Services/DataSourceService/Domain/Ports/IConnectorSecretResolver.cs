namespace DataSourceService.Domain.Ports;

// FR-01, UC-04, NFR-18, 09_datasource-connectors（fixed）§認証・秘匿情報, [[IADR-0493]] 決定 1 (#458 段 S0):
// コネクタの資格情報を**実行時に**解決するポート。
//
// 計画は「すべての接続情報 … は HashiCorp Vault で集中管理し、**コネクタは実行時に取得する**」と定める。
// 従前の 3 コネクタ（wiki / saas / db）は `DataSource.Config` の平文を**直接**読んでいた（IADR-0295 が
// 応答のマスクで塞いだのは「外へ出る経路」だけで、「読む経路」は平文のままだった）。
// 本ポートは**読む経路をこの 1 本に寄せる**ための器である。
//
// ■ 入力は `Config` に保存された値そのもの
//   参照（`vault:<path>#<key>`。`ConnectorSecretReference`）か、移送期間の平文のどちらかである。
//   どちらであるかの判定は実装が行う（呼び出し側は値の形を見ない）。
//
// ■ 🔴 **失敗は例外ではなく値で返す**
//   例外のメッセージは秘密や参照の内部（Vault のパス）を運び得る（IADR-0295 決定 4 と同じ理由）。
//   失敗の理由は `ConnectorSecretFailure` の列挙だけで運び、自由文を運ばない。
//   それでも実装が例外を投げたときは、同期サービスが型名だけを記録して fail-closed に倒す。
//
// ■ 実装（段 S0 時点）
//   - `PlaintextPassthroughConnectorSecretResolver` —— 移送期間用。平文はそのまま返し、
//     `vault:` で始まる値は**解決器が無い**として失敗させる（平文として外へ送らない＝fail-closed）。
//   - `VaultConnectorSecretResolver`（段 S1・[[IADR-0495]]）—— `Vault:Address` が在る構成で配線する。
//     参照は Vault の KV v2（専用接頭辞 `datasource/`）から読み、平文は移送期間として素通しへ委ねる。
public interface IConnectorSecretResolver
{
    /// <summary>
    /// `Config` に保存された値（参照または移送期間の平文）を、コネクタが外部へ渡す値へ解決する。
    /// 解決できないときは <see cref="ConnectorSecretResolution.Succeeded"/> が false。
    /// </summary>
    Task<ConnectorSecretResolution> ResolveAsync(string configuredValue, CancellationToken ct);
}

// [[IADR-0493]] 決定 3: 解決の失敗の理由。**自由文を持たない**（ログ・`SyncError` へ出せるのはこの符号だけ）。
public enum ConnectorSecretFailure
{
    // `vault:` で始まるが `vault:<path>#<key>` の形を成していない。
    MalformedReference,

    // 参照を解決できる実装が配備されていない（段 S0 の移送期間用の解決器に参照が来た）。
    ResolverUnavailable,

    // 参照先に値が無い（パス・キー・版が無い）。
    NotFound,

    // 解決した値が空だった。**空を「認証なし」として送らない**（fail-closed）。
    Empty,

    // 解決器そのものが失敗した（不達・権限・予期しない例外）。
    Unreachable,
}

// [[IADR-0493]] 決定 3: 解決の結果。
//
// 🔴 **record にしない。** record の既定の `ToString` は値を展開するため、構造化ログへ
// うっかり渡すと秘密がそのまま出る。`ToString` は値を伏せる。
public sealed class ConnectorSecretResolution
{
    private ConnectorSecretResolution(string? value, ConnectorSecretFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public bool Succeeded => Failure is null;

    // 成功時だけ非 null。
    public string? Value { get; }

    // 失敗時だけ非 null。
    public ConnectorSecretFailure? Failure { get; }

    public static ConnectorSecretResolution Resolved(string value) => new(value, null);

    public static ConnectorSecretResolution Failed(ConnectorSecretFailure failure) => new(null, failure);

    public override string ToString()
        => Succeeded ? $"Resolved({SecretMask.Placeholder})" : $"Failed({Failure})";
}
