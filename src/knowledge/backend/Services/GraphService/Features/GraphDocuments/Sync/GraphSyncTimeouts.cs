using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace GraphService.Features.GraphDocuments.Sync;

// FR-17, ADR-0027, ADR-0033 (#1640): グラフ同期の受け口（`GraphDocumentSyncConsumer`）の時間の上限。
//
// 🔴 受け口の ct は Wolverine の 1 通ごとの実行期限（本受け口は方針を入れないので既定 60 秒）を含む。本文の取得
// （`StorageContentReader`。http(s) は `HttpClient` 既定の 100 秒、`storage://` は S3 共通クライアント）は従前 60 秒より長い期限に頼り、
// 止まった依存先はいつも受け口の ct で「取り消し」として切られていた。外への呼び出しは本文の取得 1 回だけ（残りは DB）なので、
// 最悪の所要時間は 60 秒を正当に超えない。実行期限の方針は入れず、本文の期限が既定の実行期限に収まることを起動時に検査する。
public sealed record GraphSyncTimeouts(TimeSpan ContentRead)
{
    public const string ContentReadKey = "Graph:ContentReadTimeoutSeconds";
    public const int DefaultContentReadSeconds = 20;

    // 計器・ログに載せる呼び出し先の名前（閉じた値域）。
    public const string ContentTarget = "content";

    public static GraphSyncTimeouts Default { get; } = new(TimeSpan.FromSeconds(DefaultContentReadSeconds));

    public static GraphSyncTimeouts From(IConfiguration configuration)
    {
        var timeouts = new GraphSyncTimeouts(
            ConsumerHandlerTimeouts.Seconds(configuration, ContentReadKey, DefaultContentReadSeconds));

        ConsumerHandlerTimeouts.EnsureFits(GraphDocumentSyncConsumer.StepName,
            ConsumerHandlerTimeouts.WolverineDefault, "Wolverine の既定の実行期限",
            ($"本文の取得（{ContentReadKey}）", timeouts.ContentRead));

        return timeouts;
    }
}
