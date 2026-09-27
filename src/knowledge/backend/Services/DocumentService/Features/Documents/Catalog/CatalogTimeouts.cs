using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace DocumentService.Features.Documents.Catalog;

// FR-12, FR-06, ADR-0027, ADR-0050 (#1657): 正規化文書のカタログ登録の受け口（`DocumentNormalizedConsumer`）の時間の上限。
//
// 🔴 本受け口は **MassTransit** の受け口である（移行期間中、DocumentService の購読だけが MassTransit に残る）。
// Wolverine の受け口と違い、1 通ごとの実行期限は無い（`UseTimeout` も RabbitMQ の `ConsumerTimeout` も設定していない）ので、
// 受け口の ct はバスの停止でしか立たない。従前の本文の取得（S3 共通クライアント）は期限を持たず、止まったストレージは
// 時間切れとしても取り消しとしても記録されない「長い待ち」として受け口を占有していた。そこで次を持つ:
//
//   1. 呼び出しごとの期限 —— 本文の取得（`ContentRead`。既定 20 秒＝取り込みの `Ingestion:ContentReadTimeoutSeconds` と同じ）。
//   2. 🔴 **1 回の配信の再試行の連鎖**（試行上限 4 × 本文の期限 ＋ 試行間の待ち 42 秒）が、ブローカの `consumer_timeout`
//      （`Messaging:BrokerConsumerTimeoutSeconds`・既定 1800 秒）より短いこと（`ConsumerHandlerTimeouts.EnsureRetryChainFits`）。
//      MassTransit の `UseMessageRetry` はメモリ内の再試行で、同じ配信の中で回り ack は最後の試行の後である。
//      式に入るのは本受け口が期限で抑えている部分（本文の取得）だけで、DB（Npgsql のコマンド期限）と発行は射程外である。
//      既定では 4 × 20 ＋ 42 ＝ 122 秒で、1 試行あたり 419 秒が DB と発行の余白として残る。
public sealed record CatalogTimeouts(TimeSpan ContentRead)
{
    // DocumentService の構成節は機能ごとに `Document` を前置する（`DocumentRead:` / `DocumentTagWrite:`）。項目名は他サービスの
    // 同じ期限（`Ingestion:` / `Graph:` / `Wiki:` の `ContentReadTimeoutSeconds`）に揃える。
    public const string ContentReadKey = "DocumentCatalog:ContentReadTimeoutSeconds";
    public const int DefaultContentReadSeconds = 20;

    // 計器・ログに載せる呼び出し先の名前（閉じた値域）。
    public const string ContentTarget = "content";

    public static CatalogTimeouts Default { get; } = new(TimeSpan.FromSeconds(DefaultContentReadSeconds));

    // 構成から読み、再試行の連鎖がブローカの consumer_timeout に収まることを検査する（収まらなければ InvalidOperationException＝起動失敗）。
    public static CatalogTimeouts From(IConfiguration configuration)
    {
        var timeouts = new CatalogTimeouts(
            ConsumerHandlerTimeouts.Seconds(configuration, ContentReadKey, DefaultContentReadSeconds));

        ConsumerHandlerTimeouts.EnsureRetryChainFits(DocumentNormalizedConsumer.StepName, timeouts.ContentRead,
            $"本文の取得の期限（{ContentReadKey}。MassTransit の受け口は実行期限を持たない）",
            MassTransitExtensions.MaxAttempts, MassTransitExtensions.TotalRetryCooldown,
            ConsumerHandlerTimeouts.BrokerConsumerTimeout(configuration));

        return timeouts;
    }
}
