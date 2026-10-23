using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Messaging;
using WikiService.Features.Wiki.RemoveDeleted;
using WikiService.Features.Wiki.SyncDocument;

namespace WikiService.Features.Wiki;

// FR-13, UC-07, ADR-0027, IADR-0021 (#1640): Wiki.js への同期（`DocumentSyncConsumer`）と撤去（`DocumentDeletedConsumer`）の
// 受け口の時間の上限。
//
// 🔴 受け口の ct は Wolverine の 1 通ごとの実行期限（両受け口とも方針を入れないので既定 60 秒）を含む。本文の取得
// （`StorageMarkdownReader`）と Wiki.js の GraphQL（`WikiJsGraphQlClient`）は従前 `HttpClient` 既定の 100 秒に頼り、
// 止まった依存先はいつも受け口の ct で「取り消し」として切られていた。
//
// - 本文の取得: 1 回。
// - Wiki.js: ポートの 1 回の呼び出し（`UpsertPageAsync` / `ArchivePageAsync` / `DeletePageAsync`）は GraphQL の要求を
//   2 回（パスで ID を引く ＋ 変更）出す。期限は**ポートの 1 回の呼び出しごと**に与える（2 回の要求をまとめて抑える）。
// - ［2026-10-20 / #1971・[[IADR-0563]]］Wiki.js を呼ぶ前に書き込みの門（`WikiJsWriteGate`）を待つ。待ちの期限は Wiki.js 1 回と同じ長さである。
// 同期の最悪の所要時間は「本文 ＋ 門の待ち ＋ Wiki.js 1 回」、撤去・アーカイブは「門の待ち ＋ Wiki.js 1 回」で、既定では 50 秒と 30 秒である。
// ~~どちらも 60 秒を正当に超えないので実行期限の方針は入れず、既定の実行期限に収まることを起動時に検査する。~~
// - ［2026-10-23 / #2100・[[IADR-0599]] 決定 3・4］🔴 **門はレプリカをまたいで効くようになった**（helm の既定は HPA の minReplicas 2）。
//   門の待ちは「自分より前に並ぶ書き込みの数 × Wiki.js 1 回の期限」まで延び得る。2 レプリカの最悪は、同じレプリカの枠の持ち主が
//   他レプリカの書き込み 1 回を DB のロックで待ち、その後に自分で 1 回書く＝ 2 回分である。だから門の待ちの期限を別の値
//   `WriteGateWait`（`Wiki:WriteGateWaitSeconds`。未設定なら Wiki.js 1 回の期限の 2 倍）にし、両受け口の実行期限を
//   `Handler`（`Wiki:HandlerTimeoutSeconds`。既定 90 秒）として方針で与える。最悪の所要時間（既定: 同期 20＋30＋15＝65 秒・
//   撤去 30＋15＝45 秒）がそれに収まること、再試行の連鎖（4 × 90＋42＝402 秒）がブローカの consumer_timeout に収まることを起動時に検査する。
public sealed record WikiSyncTimeouts(TimeSpan ContentRead, TimeSpan WikiJs)
{
    public const string ContentReadKey = "Wiki:ContentReadTimeoutSeconds";
    public const string WikiJsKey = "Wiki:WikiJsTimeoutSeconds";
    public const string WriteGateWaitKey = "Wiki:WriteGateWaitSeconds";
    public const string HandlerKey = "Wiki:HandlerTimeoutSeconds";

    public const int DefaultContentReadSeconds = 20;
    public const int DefaultWikiJsSeconds = 15;
    public const int DefaultHandlerSeconds = 90;

    // 門の待ちの既定は Wiki.js 1 回の期限の何倍か（2 レプリカの最悪 ＝ 他レプリカの 1 回 ＋ 同じレプリカの 1 回）。
    public const int DefaultWriteGateWaitMultiple = 2;

    // 門の待ちの期限（プロセスの中の枠 ＋ レプリカをまたぐ枠）。未設定なら Wiki.js 1 回の期限の 2 倍。
    public TimeSpan WriteGateWait { get; init; } = WikiJs * DefaultWriteGateWaitMultiple;

    // 両受け口（`wiki-service.DocumentUpdated` / `wiki-service.DocumentDeleted`）の実行期限（`HandlerExecutionTimeoutPolicy` で与える）。
    public TimeSpan Handler { get; init; } = TimeSpan.FromSeconds(DefaultHandlerSeconds);

    // 計器・ログに載せる呼び出し先の名前（閉じた値域）。
    public const string ContentTarget = "content";
    public const string WikiJsTarget = "wiki-js";
    public const string WikiJsGateTarget = "wiki-js-gate";

    public static WikiSyncTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(DefaultContentReadSeconds), TimeSpan.FromSeconds(DefaultWikiJsSeconds));

    public static WikiSyncTimeouts From(IConfiguration configuration)
    {
        var wikiJs = ConsumerHandlerTimeouts.Seconds(configuration, WikiJsKey, DefaultWikiJsSeconds);
        var timeouts = new WikiSyncTimeouts(
            ConsumerHandlerTimeouts.Seconds(configuration, ContentReadKey, DefaultContentReadSeconds),
            wikiJs)
        {
            WriteGateWait = ConsumerHandlerTimeouts.Seconds(configuration, WriteGateWaitKey,
                (int)wikiJs.TotalSeconds * DefaultWriteGateWaitMultiple),
            Handler = ConsumerHandlerTimeouts.Seconds(configuration, HandlerKey, DefaultHandlerSeconds),
        };

        var handlerSource = $"受け口の実行期限（{HandlerKey}）";
        ConsumerHandlerTimeouts.EnsureFits(DocumentSyncConsumer.StepName,
            timeouts.Handler, handlerSource,
            ($"本文の取得（{ContentReadKey}）", timeouts.ContentRead),
            ($"Wiki.js への書き込みの門の待ち（{WriteGateWaitKey}）", timeouts.WriteGateWait),
            ($"Wiki.js への反映（{WikiJsKey}）", timeouts.WikiJs));
        ConsumerHandlerTimeouts.EnsureFits(DocumentDeletedConsumer.StepName,
            timeouts.Handler, handlerSource,
            ($"Wiki.js への書き込みの門の待ち（{WriteGateWaitKey}）", timeouts.WriteGateWait),
            ($"Wiki.js からの撤去（{WikiJsKey}）", timeouts.WikiJs));

        // 再試行の連鎖（試行 4 回 × 実行期限 ＋ 待ち 42 秒）がブローカの consumer_timeout より短いこと（IngestionTimeouts と同じ検査）。
        var brokerConsumerTimeout = ConsumerHandlerTimeouts.BrokerConsumerTimeout(configuration);
        foreach (var step in new[] { DocumentSyncConsumer.StepName, DocumentDeletedConsumer.StepName })
            ConsumerHandlerTimeouts.EnsureRetryChainFits(step, timeouts.Handler, HandlerKey,
                WolverineExtensions.MaxAttempts, WolverineExtensions.TotalRetryCooldown, brokerConsumerTimeout);

        return timeouts;
    }
}
