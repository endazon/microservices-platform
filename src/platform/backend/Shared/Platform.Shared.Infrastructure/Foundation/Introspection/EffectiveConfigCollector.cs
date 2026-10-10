using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, NFR-16, ADR-0018, ADR-0029, ADR-0075, 計画 ADR-0089 決定 1, IADR-0029, IADR-0462, [[IADR-0533]]
// (#1514, #1517, #1255 経路 ⑤): 構成情報 API が使う収集器。
//
// 宛先の集合は構成 `Introspection:Services`（service 名 → **gRPC（h2c）アドレス**）で開き、各宛先を
// `GrpcServiceIntrospectionCollector` で収集する。
//
// ［2026-10-10 / #1517・[[IADR-0533]] 決定 3］**REST（`GET /internal/introspection`）の収集は撤去した。**
// 従前は `Introspection:Services`（REST）と `Introspection:GrpcServices`（gRPC）の 2 つの構成のキーの和を宛先とし、
// gRPC 側に在る宛先だけを gRPC で集めていた（並走中の正は REST）。いまは構成キーを `Introspection:Services` の
// 1 つへ一本化し、値は gRPC の宛先である。旧キー `Introspection:GrpcServices` が残っていたら起動を止める
// （`ConfigInspectionExtensions`。黙って無視すると、上書き値の取り違えが「到達不能」としてしか現れない）。
public sealed class EffectiveConfigCollector(
    GrpcServiceIntrospectionCollector grpc,
    IOptions<IntrospectionOptions> options) : IEffectiveConfigCollector
{
    private readonly IntrospectionOptions _options = options.Value;

    public async Task<EffectiveCollection> CollectAsync(CancellationToken ct = default)
    {
        var targets = _options.ConfiguredServices();

        // FR-15: 並列に収集する（応答時間が対象サービス数に比例して増えないよう Task.WhenAll でまとめる。
        // 集約は完了後に単一スレッドで行い競合を避ける）。
        var results = await Task.WhenAll(targets.Select(async kv =>
            (Service: kv.Key, Report: await grpc.CollectOneAsync(kv.Key, kv.Value, ct))));

        return Aggregate(results);
    }

    // FR-15: 収集結果の集約。応答した宛先だけを Services / ReachableServices へ、応答しなかった宛先を
    // UnreachableServices へ入れる（適用漏れと到達不能を区別する。IADR-0029）。
    internal static EffectiveCollection Aggregate(
        IEnumerable<(string Service, ServiceIntrospectionDto? Report)> results)
    {
        var services = new List<ServiceIntrospectionDto>();
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var unreachable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (service, report) in results)
        {
            if (report is null)
            {
                unreachable.Add(service);
                continue;
            }
            services.Add(report);
            reachable.Add(service);
        }

        return new EffectiveCollection(services, reachable, unreachable);
    }
}
