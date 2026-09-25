using System.Net;
using Microsoft.Extensions.Logging;

namespace Platform.Shared.Infrastructure.Foundation.Authz;

// FR-05, NFR-09, ADR-0004, ADR-0036, [[IADR-0379]] 決定 5, [[IADR-0413]] (#1378):
// 認可スコープ解決の **REST 経路**が deny-by-default へ縮退した理由を WARN で出す。
//
// ■ なぜ要るのか
//   gRPC 経路（`AuthzScopeGrpcClient`）は輸送の失敗を WARN で出してから deny へ倒すが、
//   REST 経路（**並走中の正**）は非 2xx・不達・空本文を**無言で** deny へ畳んでいた。
//   s2s トークンの取得失敗も `ServiceTokenHandler` が `HttpRequestException` へ畳むので同じ無言の枝へ落ちる。
//   稼働環境では「一覧 0 件」「作成 403」としか見えず、#1378 の切り分けに 1 時間以上を要した。
//
// ■ なぜ 1 か所なのか
//   REST 実装は 5 つある（BFF / AiAnalysis / Graph / Retrieval / Wiki）。同じ文言を 5 か所へ写すと、
//   運用者が grep する語が実装ごとにずれる（#1323 で属性抽出が 6 か所に散ったのと同型）。
//
// 🔴 **`Granted=false`（正当な deny）では呼ばない。** 通常操作でログが溢れ、本当の縮退が埋もれる。
// 🔴 **利用者 ID・属性は載せない。** gRPC 側の WARN と同じく、状態と例外の型だけを載せる。
public static class AuthzScopeRestLog
{
    /// <summary>認可サービスが非 2xx を返した（`ServiceCaller` の門・値域外の action・認可サービスの障害など）。</summary>
    public static void NonSuccess(ILogger logger, HttpStatusCode status) =>
        logger.LogWarning(
            "認可スコープの REST 解決に失敗しました（HTTP {Status}）。deny-by-default へ縮退します。",
            (int)status);

    /// <summary>
    /// 認可サービスへ届かなかった（接続失敗・タイムアウト・s2s トークン取得失敗）。
    /// 例外本体を添える —— s2s トークン取得失敗は内側の例外に理由がある。
    /// </summary>
    public static void TransportFailure(ILogger logger, Exception ex) =>
        logger.LogWarning(ex,
            "認可スコープの REST 解決で認可サービスへ届きませんでした（{ErrorType}）。deny-by-default へ縮退します。",
            ex.GetType().Name);

    /// <summary>2xx だが本文が空（JSON の null）だった。</summary>
    public static void EmptyBody(ILogger logger, HttpStatusCode status) =>
        logger.LogWarning(
            "認可スコープの REST 応答本文が空でした（HTTP {Status}）。deny-by-default へ縮退します。",
            (int)status);
}
