using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, NFR-16, ADR-0018, ADR-0029, ADR-0075, IADR-0029, IADR-0379 決定 5, IADR-0462 (#1514, #1255 経路 ⑤):
// 構成情報 API が使う収集器。**宛先ごとに輸送を選ぶ。**
//
// 🔴 **扇形の経路は宛先単位で移る。** 宛先の集合は構成（`Introspection:Services` /
// `Introspection:GrpcServices`）で開き、各宛先が gRPC 面を持つかどうかは配備で決まる。
// 経路全体を 1 つのスイッチで切り替える形にすると、gRPC 面をまだ持たない宛先が 1 つでもある間は
// 切り替えられない（あるいは切り替えた瞬間にその宛先だけが恒久的に到達不能になる）。
//
// 宛先の集合 = 2 つの構成のキーの和。`GrpcServices` に（空でない）アドレスが在る宛先は gRPC、
// それ以外は REST。**並走中の正は REST**（IADR-0379 決定 5）—— 戻すのは gRPC 側の項目を消すだけ。
// 集約は REST だけの収集と**同じ 1 つ**（`HttpEffectiveConfigCollector.Aggregate`）を通る。
public sealed class EffectiveConfigCollector : IEffectiveConfigCollector
{
    private readonly HttpEffectiveConfigCollector _http;
    private readonly GrpcServiceIntrospectionCollector? _grpc;
    private readonly IntrospectionOptions _options;

    public EffectiveConfigCollector(
        HttpEffectiveConfigCollector http,
        IOptions<IntrospectionOptions> options,
        GrpcServiceIntrospectionCollector? grpc = null)
    {
        _http = http;
        _grpc = grpc;
        _options = options.Value;

        // 🔴 gRPC の宛先が構成されているのに gRPC の収集器が居ないのは登録の誤りである。
        // 黙って REST へ倒すと「gRPC へ移したつもりで REST のまま」になり、REST の口を退役させた
        // 段で初めて到達不能として現れる。起動の時点で落とす。
        if (_grpc is null && _options.ConfiguredGrpcServices().Count > 0)
            throw new InvalidOperationException(
                "Introspection:GrpcServices が構成されていますが gRPC の収集器が登録されていません"
                + "（AddPlatformConfigInspection を経由せずに EffectiveConfigCollector を組み立てていないか確かめること）。");
    }

    public async Task<EffectiveCollection> CollectAsync(CancellationToken ct = default)
    {
        var grpcTargets = _options.ConfiguredGrpcServices();
        var targets = _options.Services.Keys
            .Concat(grpcTargets.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 並列に収集する（HttpEffectiveConfigCollector と同じ理由。応答時間を宛先数に比例させない）。
        var results = await Task.WhenAll(targets.Select(async service =>
        {
            var report = grpcTargets.TryGetValue(service, out var address)
                ? await _grpc!.CollectOneAsync(service, address, ct)
                : await _http.CollectOneAsync(service, _options.Services[service], ct);
            return (service, report);
        }));

        return HttpEffectiveConfigCollector.Aggregate(
            results.Select(r => (r.service, (ServiceIntrospectionDto?)r.report)));
    }
}
