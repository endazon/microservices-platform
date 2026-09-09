using DocumentService.Common.Observability;
using DocumentService.Domain.Ports;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.Notification.V1;

namespace DocumentService.Infrastructure.ExternalServices;

// FR-19, FR-20, FR-21, FR-22, NFR-09, NFR-16, NFR-19, ADR-0004, ADR-0029, ADR-0037 決定 6・17・18,
// ADR-0045 決定 8, ADR-0075, [[IADR-0215]] 決定 3・5-a・5-b, [[IADR-0267]], [[IADR-0270]] 決定 6,
// [[IADR-0379]] 決定 4・5, [[IADR-0401]], [[IADR-0408]], [[IADR-0412]] 決定 5, [[IADR-0417]] 決定 9,
// [[IADR-0419]] (#1255): NotificationService への送出アダプタの **gRPC 版**。
//
// **並走中の正は REST である。** 本実装は `Services:NotificationServiceGrpc` が構成されたときだけ
// 登録され（`AddNotificationIngressGrpcClient`）、無ければ `HttpPrivateNoteNotifier` のままである。
// 戻すのは構成を外すだけでよい（コードは変えない）。
//
// 🔴 **利用者の資格情報は載せない**（[[IADR-0379]] 決定 4）。載るのは本サービス自身の s2s トークンだけ
// である。これが成立するのは、**この経路が現状も利用者の資格情報を運んでいない**からである ——
// 発火の 3 契機（同期 push・完全削除・定期処理）はいずれも「誰へ・何件」を**本サービスが検知**して
// 送るのであり、受け手は本文の `subject` だけで宛先を決める（[[IADR-0270]] 決定 6）。
// **移行で判定の位置を動かしていない。**
//
// 🔴 **縮退の枝を 1 つも増やさず・1 つも減らさない**（[[IADR-0408]] と同じ規律）。
// `HttpPrivateNoteNotifier` の結末は 3 つで、対応は次のとおりである。
//
// | 事象 | REST | ここ | 計器 |
// | --- | --- | --- | --- |
// | 受理された（新規・重複のどちらも） | 2xx | 例外なし | `sent` |
// | 受け口へ届いたが拒否された | 非 2xx | `RpcException`（下の「不達」以外の status） | `rejected` |
// | 受け口へ届かなかった | 例外（通信・タイムアウト・想定外） | `UNAVAILABLE` / `DEADLINE_EXCEEDED` ／ s2s トークン取得失敗 | `unreachable` |
// | 呼び出し元のキャンセル | **伝播させる** | 同じ | 数えない |
//
// 🔴 **全 status を「不達」へ畳まない**（[[IADR-0417]] 決定 9 と同じ理由）。畳むと
// **`rejected` の枝が静かに消え**、ペイロード・配備の不整合（検証違反・realm ロールの欠落・
// 受け口の 500）が「届かなかった」に見える。計器の説明文がその 2 つを分けているのは、
// **打つ手が違う**からである（前者は直す・後者は待つ／繋ぐ）。
// 逆に `UNAUTHENTICATED` / `PERMISSION_DENIED` を「不達」に入れないのも同じ理由である ——
// **要求は届いており、拒んだのは受け口である**（realm の service account の配線漏れがここに出る）。
//
// ★ タイムアウト: REST 側は `HttpClient.Timeout = SendTimeout`（5 秒）で与えている。gRPC には
// `HttpClient` が無いので **`deadline` で同じ 5 秒を与える**（`HttpPrivateNoteNotifier.SendTimeout` を
// **そのまま引く** —— 値を書き写すと片方だけ動いたときに気付けない）。期限切れは
// `RpcException(DeadlineExceeded)` であり、上の「不達」枝になる（REST の `TaskCanceledException` と同値）。
public sealed class GrpcPrivateNoteNotifier(
    Pb.NotificationIngress.NotificationIngressClient client,
    PrivateNoteNotificationMetrics metrics,
    TimeProvider clock,
    ILogger<GrpcPrivateNoteNotifier> logger) : IPrivateNoteNotifier
{
    public async Task NotifyAsync(string subject, string kind, DateTimeOffset occurredAt,
        int? count = null, int? thresholdPercent = null, DateTimeOffset? deadline = null,
        CancellationToken ct = default)
    {
        try
        {
            await client.AcceptAsync(
                ToRequest(subject, kind, occurredAt, count, thresholdPercent, deadline),
                deadline: clock.GetUtcNow().UtcDateTime.Add(HttpPrivateNoteNotifier.SendTimeout),
                cancellationToken: ct);

            metrics.RecordDispatch(kind, PrivateNoteNotificationMetrics.OutcomeSent);
        }
        catch (RpcException ex)
        {
            // 🔴 status で「届かなかった」と「届いたが拒まれた」を分け直す（上の表）。
            var outcome = IsUnreachable(ex.StatusCode)
                ? PrivateNoteNotificationMetrics.OutcomeUnreachable
                : PrivateNoteNotificationMetrics.OutcomeRejected;
            metrics.RecordDispatch(kind, outcome);
            logger.LogError(ex,
                "通知の送出に失敗しました（status={Status}）。kind={Kind} subject は本文へ出さない。",
                ex.StatusCode, kind);
        }
        // ★ 呼び出し元のキャンセル以外は**すべて**握る（fail-open。REST 側と同じ姿勢・同じ理由）。
        // s2s トークンの取得失敗（`InvalidOperationException`）もここへ落ちる ——
        // **後段へ 1 バイトも届いていない**ので `unreachable` である。
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            metrics.RecordDispatch(kind, PrivateNoteNotificationMetrics.OutcomeUnreachable);
            logger.LogError(ex,
                "通知の送出に失敗しました（受け口へ到達できない）。kind={Kind} subject は本文へ出さない。",
                kind);
        }
    }

    // 🔴 **null は presence を立てないことで運ぶ**（[[IADR-0408]] 決定 3 / [[IADR-0419]] 決定 2）。
    // 代入してしまうと proto3 の既定（`0`）が「値」として届き、**検証は通る**（`is not < 0` は
    // null にも 0 にも真）ので例外は 1 つも起きない。割れるのは受け口の重複判定
    // （`n.Count == request.Count`）だけであり、**同じ事象が新規として二重に積まれる**。
    internal static Pb.AcceptRequest ToRequest(
        string subject, string kind, DateTimeOffset occurredAt,
        int? count, int? thresholdPercent, DateTimeOffset? deadline)
    {
        var request = new Pb.AcceptRequest
        {
            Subject = subject,
            Kind = kind,
            OccurredAt = Timestamp.FromDateTimeOffset(occurredAt),
        };
        if (count is { } c) request.Count = c;
        if (thresholdPercent is { } p) request.ThresholdPercent = p;
        if (deadline is { } d) request.Deadline = Timestamp.FromDateTimeOffset(d);
        return request;
    }

    // 「後段へ届いていない」だけを不達とする。**それ以外は受け口が答えた失敗である。**
    private static bool IsUnreachable(StatusCode status) =>
        status is StatusCode.Unavailable or StatusCode.DeadlineExceeded;

    // シャットダウン・利用者の切断は業務処理の側の事情であり、通知の失敗ではない（REST 版と同じ）。
    private static bool IsCallerCancellation(Exception ex, CancellationToken ct)
        => ex is OperationCanceledException && ct.IsCancellationRequested;
}

// FR-22, NFR-09, NFR-16, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5, [[IADR-0402]] 決定 6,
// [[IADR-0412]] 決定 5, [[IADR-0419]] (#1255): NotificationService 宛の生成クライアントの登録。
public static class NotificationIngressGrpcClientExtensions
{
    /// <summary>`Services:NotificationServiceGrpc`（h2c のアドレス。例: http://notification-service:8081）。</summary>
    public const string AddressKey = "Services:NotificationServiceGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（[[IADR-0402]] 決定 6）。</summary>
    public const string ChannelKey = "NotificationServiceGrpc";

    // 構成が無ければ**何も登録しない** —— 呼び出し元は登録の有無で REST 実装と gRPC 実装を選ぶ。
    public static IServiceCollection AddNotificationIngressGrpcClient(
        this IServiceCollection services, IConfiguration config)
    {
        var address = config[AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        // 🔴 **DocumentService が east-west gRPC の「呼び出し元」になるのはこれが最初である。**
        // 従前は受け口（`DocumentRead` / `DocumentTagWrite` / `TagDictionary`）しか持っておらず、
        // s2s トークンの発行側は 1 度も登録されていなかった ——
        // したがって realm の confidential client `document-service` と
        // `ServiceToken__*` の注入が**この PR で新しく要る**（配備 4 経路をすべて揃えること）。
        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き ＋ `TryAdd`**（[[IADR-0402]] 決定 6 / [[IADR-0412]] 決定 5）。
        // 今は宛先が 1 つだけだが、キー無しで置くと次の宛先が足された瞬間に
        // **どちらかのクライアントがもう片方の宛先へ繋がる**（GraphService / BFF が実測した形）。
        // `Add` にすると二重登録が障害としては現れず、規約だけが静かに破れる。
        services.TryAddKeyedSingleton<GrpcChannel>(ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.NotificationIngress.NotificationIngressClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        return services;
    }
}
