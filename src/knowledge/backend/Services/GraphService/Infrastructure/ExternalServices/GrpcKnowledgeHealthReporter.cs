using Grpc.Core;
using Grpc.Net.Client;
using GraphService.Common.Observability;
using GraphService.Domain.Ports;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Knowledge.Contracts.Grpc.Dashboard.V1;

namespace GraphService.Infrastructure.ExternalServices;

// FR-10, FR-17, FR-19, NFR-09, NFR-16, NFR-21, UC-05, SC-10, ADR-0002, ADR-0006, ADR-0029, ADR-0075,
// ADR-0076, [[IADR-0256]] 決定 3, [[IADR-0265]], [[IADR-0299]], [[IADR-0353]], [[IADR-0379]] 決定 4・5,
// [[IADR-0389]] 決定 5, [[IADR-0408]] (#1255): ナレッジ健全性の観測値の送出アダプタの **gRPC 版**。
//
// **並走中の正は REST である。** 本実装は `Services:DashboardServiceGrpc` が構成されたときだけ
// 登録され（`AddKnowledgeHealthGrpcClient`）、無ければ `HttpKnowledgeHealthReporter` のままである。
// 戻すのは構成を外すだけでよい（コードは変えない）。
//
// 🔴 **利用者の資格情報は載せない**（[[IADR-0379]] 決定 4）。載るのは本サービス自身の s2s トークンだけである。
// これが成立するのは、**この経路が現状も利用者の資格情報を運んでいない**からである ——
// 生産者は利用者 JWT を持たない定期処理であり（[[IADR-0299]] 決定 4 が受け口の認証を外した理由そのもの）、
// 個人資料を集計から外す判定は**受け手**が持つ（[[IADR-0265]]）。**移行で判定の位置を動かしていない。**
//
// 🔴 **縮退の枝を 1 つも増やさず・1 つも減らさない。** `HttpKnowledgeHealthReporter` の枝は 3 つで、
// 対応は次のとおりである（値も副作用も同じ）。
//
// | 事象 | REST | ここ | 副作用 |
// | --- | --- | --- | --- |
// | 受理された | 2xx | 例外なし | `metrics.RecordDelivered(indicator)` **のみ** |
// | 受理されない | 非 2xx → LogError（status つき） | `RpcException` → LogError（StatusCode つき） | **数えない・投げない** |
// | 到達できない・トークン取得失敗 | 例外 → LogError（到達できない） | `RpcException`(UNAVAILABLE) / `InvalidOperationException` | 同上 |
// | 呼び出し元のキャンセル | **伝播させる** | 同じ | — |
//
// 🔴 **故障を「該当なし」に化けさせない**（[[IADR-0256]] 決定 3 / [[IADR-0389]] 決定 5）——
// 失敗時に `RecordDelivered` を呼ばないことが `absent` 系アラートの土台である。
// **試みた回数を数える形へ変えてはならない**（受け口が死んでいる間も系列が生き続け、不在が鳴らない）。
//
// ★ タイムアウト: REST 側は `HttpClient.Timeout = SendTimeout`（5 秒）で与えている。gRPC には
// `HttpClient` が無いので **`deadline` で同じ 5 秒を与える**（`HttpKnowledgeHealthReporter.SendTimeout`
// を**そのまま引く** —— 値を書き写すと片方だけ動いたときに気付けない）。期限切れは
// `RpcException(DeadlineExceeded)` であり、上の「受理されない」枝と同じ縮退になる。
public sealed class GrpcKnowledgeHealthReporter(
    Pb.KnowledgeHealthReport.KnowledgeHealthReportClient client,
    KnowledgeHealthReportMetrics metrics,
    TimeProvider clock,
    ILogger<GrpcKnowledgeHealthReporter> logger) : IKnowledgeHealthReporter
{
    public async Task ReportAsync(
        string indicator,
        IReadOnlyList<KnowledgeHealthObservation> observations,
        int? thresholdDays = null,
        CancellationToken ct = default)
    {
        try
        {
            var request = new Pb.ReportRequest { Indicator = indicator };
            foreach (var o in observations)
            {
                var message = new Pb.Observation { SubjectKey = o.SubjectKey };
                // 🔴 null のときは**代入しない**（presence を立てない）。空文字を書くと、受け口では
                // 「個人資料ではない」（null）と区別できなくなる。
                if (o.DocScope is not null) message.DocScope = o.DocScope;
                // 🔴 同上。軸を持たない指標に `""` という軸が 1 本生まれると内訳の集計が割れる。
                if (o.Dimension is not null) message.Dimension = o.Dimension;
                request.Observations.Add(message);
            }

            // 🔴 **しきい値は持つ指標だけに載せる。** 常に載せると（proto3 の既定は 0 なので）
            // 受け口の検証器が `thresholdDays must be greater than zero` で弾き、
            // **しきい値を持たない 3 指標の報告が全部落ちる**（REST が項目そのものを出さないのと同じ理由）。
            if (thresholdDays is { } days) request.ThresholdDays = days;

            await client.ReportAsync(
                request,
                deadline: clock.GetUtcNow().UtcDateTime.Add(HttpKnowledgeHealthReporter.SendTimeout),
                cancellationToken: ct);

            // ★ [[IADR-0389]] 決定 5: **受理されたときだけ数える。**
            metrics.RecordDelivered(indicator);
        }
        catch (RpcException ex)
        {
            // REST の「非 2xx」と「到達できない」は gRPC では同じ `RpcException` に畳まれる。
            // どちらも**数えず・投げず・エラーログを残す**（現行の 2 枝と同じ値・同じ副作用）。
            logger.LogError(ex,
                "ナレッジ健全性の報告に失敗した（status={Status}）。indicator={Indicator} count={Count}。"
                + "対象の識別子は本文へ出さない。",
                ex.StatusCode, indicator, observations.Count);
        }
        // ★ 呼び出し元のキャンセル以外は**すべて**握る（fail-open。REST 側と同じ姿勢・同じ理由）。
        // s2s トークンの取得失敗（`InvalidOperationException`）もここへ落ちる ——
        // **報告できないことは業務処理の失敗ではない**（本サービスは DocumentUpdated /
        // DocumentDeleted の購読ホストでもあり、指標の送出失敗で購読を止めない）。
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            logger.LogError(ex,
                "ナレッジ健全性の報告に失敗した（受け口へ到達できない）。indicator={Indicator} count={Count}。",
                indicator, observations.Count);
        }
    }

    // シャットダウンは呼び出し元の事情であり、報告の失敗ではない。
    private static bool IsCallerCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;
}

// FR-10, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5, [[IADR-0408]] (#1255):
// DashboardService 宛の生成クライアントの登録。参照実装 `AddAuthzScopeGrpcClient` と同型。
public static class KnowledgeHealthGrpcClientExtensions
{
    /// <summary>`Services:DashboardServiceGrpc`（h2c のアドレス。例: http://dashboard-service:8081）。</summary>
    public const string AddressKey = "Services:DashboardServiceGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（下の 🔴 を参照）。</summary>
    public const string ChannelKey = "DashboardServiceGrpc";

    // 構成が無ければ**何も登録しない** —— 呼び出し元は登録の有無で REST 実装と gRPC 実装を選ぶ。
    public static IServiceCollection AddKnowledgeHealthGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き**で登録する。本サービスは既に 2 つの宛先を持ち得る ——
        // 認可サービス宛は `AddAuthzScopeGrpcClient` が**キー無し**で、LlmGateway 宛は
        // `AddLlmGatewayGrpcClient` が**キー付き**で登録する。3 つ目をキー無しで足すと、
        // 観測値の報告が**認可サービスへ繋がる**（あるいはその逆）。
        services.AddKeyedSingleton(ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.KnowledgeHealthReport.KnowledgeHealthReportClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        return services;
    }
}
