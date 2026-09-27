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
// 同期の最悪の所要時間は「本文 ＋ Wiki.js 1 回」、撤去・アーカイブは「Wiki.js 1 回」で、既定では 35 秒と 15 秒である。
// どちらも 60 秒を正当に超えないので実行期限の方針は入れず、既定の実行期限に収まることを起動時に検査する。
public sealed record WikiSyncTimeouts(TimeSpan ContentRead, TimeSpan WikiJs)
{
    public const string ContentReadKey = "Wiki:ContentReadTimeoutSeconds";
    public const string WikiJsKey = "Wiki:WikiJsTimeoutSeconds";

    public const int DefaultContentReadSeconds = 20;
    public const int DefaultWikiJsSeconds = 15;

    // 計器・ログに載せる呼び出し先の名前（閉じた値域）。
    public const string ContentTarget = "content";
    public const string WikiJsTarget = "wiki-js";

    public static WikiSyncTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(DefaultContentReadSeconds), TimeSpan.FromSeconds(DefaultWikiJsSeconds));

    public static WikiSyncTimeouts From(IConfiguration configuration)
    {
        var timeouts = new WikiSyncTimeouts(
            ConsumerHandlerTimeouts.Seconds(configuration, ContentReadKey, DefaultContentReadSeconds),
            ConsumerHandlerTimeouts.Seconds(configuration, WikiJsKey, DefaultWikiJsSeconds));

        ConsumerHandlerTimeouts.EnsureFits(DocumentSyncConsumer.StepName,
            ConsumerHandlerTimeouts.WolverineDefault, "Wolverine の既定の実行期限",
            ($"本文の取得（{ContentReadKey}）", timeouts.ContentRead),
            ($"Wiki.js への反映（{WikiJsKey}）", timeouts.WikiJs));
        ConsumerHandlerTimeouts.EnsureFits(DocumentDeletedConsumer.StepName,
            ConsumerHandlerTimeouts.WolverineDefault, "Wolverine の既定の実行期限",
            ($"Wiki.js からの撤去（{WikiJsKey}）", timeouts.WikiJs));

        return timeouts;
    }
}
