namespace DataSourceService.Domain;

// FR-01, UC-04, NFR-18, [[IADR-0493]] 決定 1・4 (#458 段 S0): 1 回の同期のために**開始時に 1 回だけ**解決した資格情報。
//
// 同期サービスが `IConnectorSecretResolver` で解決し、Discover と各 Fetch へ**同じインスタンス**を渡す。
// コネクタは資格情報を `DataSource.Config` からではなく、ここからだけ読む。
//
// ■ なぜ開始時に 1 回なのか（作業仕様書 §窓 2）
//   Discover / Fetch の呼び出しごとに解決すると、同期の途中で Vault の版が変わったとき
//   1 回の同期の中で古い版と新しい版が混ざる。開始時に 1 回なら、書き込み後に始まった同期は新しい版を、
//   書き込み前に始まった同期は古い版を最後まで使う（両方の端を満たす形はこれ 1 つ）。
//
// 🔴 **`ToString` は値を伏せる**（キー名だけを出す）。
public sealed class ConnectorCredentials
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public ConnectorCredentials(IReadOnlyDictionary<string, string> values)
        => _values = new Dictionary<string, string>(values, StringComparer.Ordinal);

    // 資格情報を 1 つも持たない（コネクタが資格情報を宣言しない、または `Config` に値が無い）。
    public static ConnectorCredentials None { get; } = new(new Dictionary<string, string>());

    // 解決済みの値。無ければ null（＝認証なしで接続する。従前の「`Config` に無ければ付けない」と同じ）。
    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public IEnumerable<string> Keys => _values.Keys;

    public override string ToString()
        => $"ConnectorCredentials[{string.Join(", ", _values.Keys.Select(k => $"{k}={SecretMask.Placeholder}"))}]";
}
