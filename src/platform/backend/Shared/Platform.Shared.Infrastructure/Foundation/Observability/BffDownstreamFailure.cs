using System.Diagnostics;
using System.Diagnostics.Metrics;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Shared.Infrastructure.Foundation.Observability;

// FR-03, NFR-06, NFR-19, NFR-28, [[IADR-0603]] (#2104): BFF が**後段の不達・時間切れを畳むとき**の計器。
//
// ■ なぜ要るのか
//   BFF の端点の多くは後段の不達を 502 や「200・0 件」へ畳み、**ログも計器も出していなかった**。
//   運用者には後段の不調が見えず、検索では利用者にも「該当なし」と区別できなかった（#2104）。
//
// ★ **1 本のカウンタの属性で分ける**（`UsageEventMetrics` と同じ考え）。端点ごとに計器を分けると、
//   新しい端点を足した人が計器を作り忘れても誰も気づかない。
// 🔴 **属性はすべて有界である。** 後段名・縮退先は呼び出し側の定数、経路はルートの型（`/bff/x/{id}`）、
//   種別は下の 4 値。**要求の生のパス・検索語・利用者 ID は載せない**（基数が非有界・本文そのもの）。
public sealed class BffDownstreamFailureMetrics
{
    // Meter 名は BFF のサービス名（Program.cs の AddMeter と OTLP の収集対象。`UsageEventMetrics` と同じ）。
    public const string MeterName = "microservices-platform.bff";

    public const string CounterName = "bff.downstream.failure.total";

    public const string DownstreamTag = "bff.downstream";
    public const string EndpointTag = "bff.endpoint";
    public const string KindTag = "bff.downstream.failure.kind";
    public const string FallbackTag = "bff.fallback";

    private readonly Counter<long> _failures;

    public BffDownstreamFailureMetrics(IMeterFactory meterFactory)
    {
        _failures = meterFactory.Create(MeterName).CreateCounter<long>(
            CounterName, unit: "{request}",
            description: "BFF が後段の不達・時間切れを縮退（502・空・SSE の error 等）へ畳んだ件数。"
                       + "bff.downstream.failure.kind = unreachable / timeout / credential / error。"
                       + "利用者の取り消しは数えない。");
    }

    public void Record(string downstream, string endpoint, string kind, string fallback)
        => _failures.Add(1, new TagList
        {
            { DownstreamTag, downstream },
            { EndpointTag, endpoint },
            { KindTag, kind },
            { FallbackTag, fallback },
        });
}

// FR-03, NFR-19, [[IADR-0603]] (#2104): 畳む箇所から呼ぶ報告口（WARN 1 行 ＋ 計器 1 件）。
// 端点は静的メソッドが多いので、ロガー・計器は要求の DI から引く（`DocumentReadFailureLog` と同じ形）。
public static class BffDownstreamFailure
{
    // 運用者が grep する語を固定する。
    public const string LogCategory = "Platform.Bff.DownstreamFailure";

    /// <summary>縮退先の語彙（計器の `bff.fallback`）。</summary>
    public static class Fallbacks
    {
        public const string BadGateway = "502";
        public const string NotFound = "404";
        public const string Empty = "empty";
        public const string SseError = "sse_error";
        public const string Omitted = "omitted";
        public const string Unknown = "unknown";
    }

    /// <summary>失敗の種別の語彙（計器の `bff.downstream.failure.kind`）。</summary>
    public static class Kinds
    {
        public const string Unreachable = "unreachable";
        public const string Timeout = "timeout";

        /// <summary>
        /// s2s トークンの取得失敗（`ClientCredentialsServiceTokenProvider` の `InvalidOperationException`）。後段へ 1 バイトも届いていない。
        /// 🔴 **例外の型からは推さない**（`InvalidOperationException` は無関係な箇所からも投げられる）。
        /// catch の述語でトークン取得の失敗だと分かっている呼び出し側だけが、引数 `kind` で明示する（<see cref="CredentialIf"/>）。
        /// </summary>
        public const string Credential = "credential";

        public const string Error = "error";

        /// <summary>
        /// 述語が「不達」として `InvalidOperationException` を通す箇所（`IsRetrievalUnreachable` / `IsTransportFailure`）用。
        /// その述語の下では同型の例外は s2s トークン取得の失敗である —— **その前提を持つ呼び出し側だけが使う**。
        /// </summary>
        public static string? CredentialIf(Exception ex) => ex is InvalidOperationException ? Credential : null;
    }

    /// <summary>
    /// WARN を 1 行出し、計器へ 1 件載せる。利用者の取り消し中なら何もしない。
    /// `kind` を省くと例外の型から推す（時間切れ・不達だけ。それ以外は `error`）。
    /// </summary>
    public static void Report(HttpContext http, string downstream, Exception ex, string fallback, string? kind = null)
    {
        if (IsCallerAborted(http))
            return;

        var (method, endpoint, inferred) = Describe(http, ex);
        kind ??= inferred;
        // 🔴 NFR-28: 例外本体・メッセージは載せない（s2s トークン取得の失敗は IdP の応答を含み得る）。
        //   載せるのはすべて有界の定数・型名である。
        LoggerOf(http).LogWarning(
            "BFF の後段 {Downstream} から結果を得られませんでした（{Kind}・{ErrorType}・{Method} {Endpoint}）。{Fallback} へ縮退します。",
            downstream, kind, ex.GetType().Name, method, endpoint, fallback);
        MetricsOf(http)?.Record(downstream, endpoint, kind, fallback);
    }

    /// <summary>計器だけ載せる（ログを別の口が既に出している箇所。`DocumentReadFailureLog`）。</summary>
    public static void Count(HttpContext http, string downstream, Exception ex, string fallback, string? kind = null)
    {
        if (IsCallerAborted(http))
            return;
        var (_, endpoint, inferred) = Describe(http, ex);
        MetricsOf(http)?.Record(downstream, endpoint, kind ?? inferred, fallback);
    }

    /// <summary>BFF のホストへ計器を登録する（Meter の収集宣言 `AddMeter(MeterName)` はホスト側に要る）。</summary>
    public static IServiceCollection AddBffDownstreamFailureMetrics(this IServiceCollection services)
    {
        services.TryAddSingleton<BffDownstreamFailureMetrics>();
        return services;
    }

    // 🔴 利用者の取り消し（切断）は後段の失敗ではない。数えると「利用者が戻る操作をしただけ」で計器が伸びる。
    private static bool IsCallerAborted(HttpContext http) => http.RequestAborted.IsCancellationRequested;

    private static string KindOf(Exception ex) => ex switch
    {
        // 呼び出し元の取り消しは上で除いているので、ここに来る取り消しは HttpClient の時間切れである。
        OperationCanceledException => Kinds.Timeout,
        RpcException { StatusCode: StatusCode.DeadlineExceeded } => Kinds.Timeout,
        RpcException { StatusCode: StatusCode.Unavailable } => Kinds.Unreachable,
        HttpRequestException => Kinds.Unreachable,
        // 🔴 `InvalidOperationException` を `credential` と推さない（呼び出し側が `kind` で明示する。Kinds.Credential）。
        _ => Kinds.Error,
    };

    private static (string Method, string Endpoint, string Kind) Describe(HttpContext http, Exception ex)
    {
        var endpoint = http.GetEndpoint() is RouteEndpoint route
            ? route.RoutePattern.RawText ?? "-"
            : "-";
        return (MethodOf(http.Request.Method), endpoint, KindOf(ex));
    }

    // 🔴 NFR-28（CWE-117・CodeQL `cs/log-forging`）: 要求の HTTP メソッドは**利用者が送った文字列**である。
    //   ログへ載せる前に既知の定数へ写す（未知の値は `OTHER`）。経路はルートの型（`RoutePattern`）であり要求由来ではない。
    private static string MethodOf(string method) =>
        HttpMethods.IsGet(method) ? HttpMethods.Get
        : HttpMethods.IsPost(method) ? HttpMethods.Post
        : HttpMethods.IsPut(method) ? HttpMethods.Put
        : HttpMethods.IsPatch(method) ? HttpMethods.Patch
        : HttpMethods.IsDelete(method) ? HttpMethods.Delete
        : HttpMethods.IsHead(method) ? HttpMethods.Head
        : "OTHER";

    private static ILogger LoggerOf(HttpContext http) =>
        http.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(LogCategory)
        ?? NullLogger.Instance;

    private static BffDownstreamFailureMetrics? MetricsOf(HttpContext http) =>
        http.RequestServices?.GetService<BffDownstreamFailureMetrics>();
}
