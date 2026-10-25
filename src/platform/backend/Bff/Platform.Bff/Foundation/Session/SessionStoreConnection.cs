using StackExchange.Redis;

namespace Platform.Bff.Foundation.Session;

// NFR, NFR-07, NFR-18, ADR-0032, IADR-0251 決定 5, IADR-0522, [[IADR-0633]] (#2242):
// セッションストア（Valkey）への**遅延接続の方針の置き場**。鍵リング（`AddBffSession`）とヘルスチェック（`Program.cs`）が同じものを使う。
//
// 🔴 **失敗を覚えない（`LazyThreadSafetyMode.PublicationOnly`）。** 既定の `ExecutionAndPublication` はファクトリの例外を覚えるので、
// 起動時の DNS の差し替えなどで初回の `Connect` が 1 度失敗すると、そのプロセスは以後ずっと例外を返す。鍵リングがそうなると
// ログインの challenge（nonce の cookie を DataProtection で保護する）が全部 500 になるのに、ヘルスチェック（こちらは失敗を覚えない）は
// 回復して Ready のままになる（#2232 と #2241 の e2e-live で実測）。方針が 2 か所で分かれていたことが原因なので、ここ 1 か所に置く。
// `Platform.Bff` の本番コードで `new Lazy<IConnectionMultiplexer>` を書いてよいのはこのファイルだけである（試験が走査で固定する）。
//
// `AbortOnConnectFail` は既定（true）のまま（IADR-0633 決定 2）: 届かなければ `Connect` が例外になり、次の利用で接続をやり直す。
// 競り合いで接続が 2 つ作られうる（`PublicationOnly` の性質）が、使われるのは 1 つで、作られるのは初回の利用の瞬間だけである。

/// <summary>鍵リングの接続を作る関数。既定は <c>ConnectionMultiplexer.Connect</c>。試験が差し替える。</summary>
public delegate IConnectionMultiplexer SessionStoreConnect(ConfigurationOptions options);

/// <summary>セッションストアへの遅延接続（失敗を覚えない）。</summary>
public static class SessionStoreConnection
{
    /// <summary>
    /// 初回の利用で <paramref name="connect"/> を呼ぶ遅延接続。**失敗は覚えない** —— 例外のあと次の利用で <paramref name="connect"/> をやり直す。
    /// 成功した接続は以後使い回す。
    /// </summary>
    public static Lazy<IConnectionMultiplexer> Lazy(Func<IConnectionMultiplexer> connect) =>
        new(connect, LazyThreadSafetyMode.PublicationOnly);
}

/// <summary>鍵リングの保存先が引く接続（DI の単一の実体。失敗を覚えない遅延を包む）。</summary>
public sealed class SessionStoreKeyRingConnection(Lazy<IConnectionMultiplexer> connection)
{
    public IConnectionMultiplexer Value => connection.Value;
}
