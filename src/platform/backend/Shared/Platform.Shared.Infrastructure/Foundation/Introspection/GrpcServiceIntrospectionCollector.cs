using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Dto = Platform.Shared.Contracts.Dtos;
using Pb = Platform.Shared.Contracts.Grpc.Introspection.V1;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, NFR-09, NFR-16, ADR-0018, ADR-0029, ADR-0075, IADR-0029, IADR-0379 決定 4・5, IADR-0462
// (#1514, #1255 経路 ⑤): 1 宛先ぶんの自己申告を **gRPC（h2c）** で収集する。
//
// ■ 資格情報: 呼び出し側（BFF）自身の s2s トークンを `CreatePlatformChannel` の CallCredentials で付ける。
//   利用者のトークンは載せない（この経路は定期処理であり、利用者文脈を 1 バイトも持たない）。
//
// ■ 🔴 **失敗の畳み方は REST と同じ**（`HttpEffectiveConfigCollector.CollectOneAsync`）。
//   全 status・s2s トークン取得失敗・期限切れ・空の申告を「到達不能」（null）へ隔離する ——
//   収集器の出力は「申告を得た / 得られなかった」の 2 値であり、ドリフト検出はそれで
//   適用漏れと到達不能を分ける（IADR-0029）。**移行の不変条件は「挙動を変えない」である。**
//   ただしログは status で分ける: `UNAUTHENTICATED` / `PERMISSION_DENIED` は**配線不備**
//   （realm の service account・`platform-service`・Secret の注入漏れ）であり、再起動では直らないので
//   Error で出す。REST には無かった失敗の種類であり、Warning に混ぜると「一過性の到達不能」に紛れる。
//
// ■ 期限（deadline）: REST のタイムアウトと**同じ `Introspection:TimeoutSeconds`** を引く。
//   既定の無期限のまま 1 宛先が応答しないと、定期検出の 1 周が止まる。
// ■ リトライ: 持たない（REST も持たない）。定期検出の次の周期が再試行である。
// ■ 取り消し: 呼び出し側の ct による取り消し（停止要求）だけを外へ出す（REST と同じ。#1382）。
//
// チャネルは宛先アドレスごとに 1 本を使い回す（HTTP/2 は多重化される。周期ごとに張り直さない）。
public sealed class GrpcServiceIntrospectionCollector(
    IServiceTokenProvider tokenProvider,
    IOptions<IntrospectionOptions> options,
    ILogger<GrpcServiceIntrospectionCollector> logger) : IDisposable
{
    private readonly IntrospectionOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.Ordinal);

    public async Task<Dto.ServiceIntrospectionDto?> CollectOneAsync(
        string service, string address, CancellationToken ct)
    {
        try
        {
            var channel = _channels.GetOrAdd(address, CreateChannel);
            var client = new Pb.ServiceIntrospection.ServiceIntrospectionClient(channel);
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _options.TimeoutSeconds));
            var report = await client.GetAsync(
                new Pb.GetServiceIntrospectionRequest(), deadline: deadline, cancellationToken: ct);

            if (string.IsNullOrEmpty(report.Service))
            {
                // 🔴 空の申告は「申告として無効」。REST の空応答（null）と同じ枝へ落とす ——
                // 空文字のサービスとして集約へ入れると、どの宣言にも突合されない申告が 1 件増える。
                logger.LogWarning(
                    "Introspection for {Service} at {Address} returned an empty service name over gRPC",
                    service, address);
                return null;
            }

            return IntrospectionGrpcMapping.ToDto(report);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 停止要求による取り消しは到達不能へ畳まない（REST と同じ）。gRPC は取り消しを
            // RpcException(Cancelled) で表すので、呼び出し側が待つ OperationCanceledException へ揃える。
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            logger.LogError(ex,
                "Introspection for {Service} at {Address} was rejected over gRPC ({Status}); "
                + "check the caller's service account and platform-service role",
                service, address, ex.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to collect introspection for {Service} at {Address} over gRPC", service, address);
            return null;
        }
    }

    private GrpcChannel CreateChannel(string address) =>
        GrpcClientExtensions.CreatePlatformChannel(address, tokenProvider);

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
            channel.Dispose();
        _channels.Clear();
    }
}
