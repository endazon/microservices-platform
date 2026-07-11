using Platform.Shared.Contracts.Dtos;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15: 各サービスの自己申告（イントロスペクション）を収集し、実効構成の素材を返す。
public interface IEffectiveConfigCollector
{
    Task<EffectiveCollection> CollectAsync(CancellationToken ct = default);
}

// 収集結果。Services は自己申告を返したサービスのみ。ReachableServices は応答した
// サービス識別子（構成キー＝pipeline.json の service 名）、UnreachableServices は
// 設定済みだが応答しなかったサービス識別子（ドリフト判定の誤検知抑制に用いる）。
public sealed record EffectiveCollection(
    IReadOnlyList<ServiceIntrospectionDto> Services,
    IReadOnlySet<string> ReachableServices,
    IReadOnlySet<string> UnreachableServices)
{
    public static EffectiveCollection Empty { get; } = new(
        [], new HashSet<string>(), new HashSet<string>());
}
