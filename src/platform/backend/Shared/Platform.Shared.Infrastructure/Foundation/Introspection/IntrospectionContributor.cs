using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Shared.Contracts.Dtos;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, FR-16, ADR-0018, IADR-0660 (#2285): 自己申告への**要求のたびの寄与**。
//
// 自己申告（`ServiceIntrospectionDto`）は起動時に 1 回だけ組み立てる静的な申告である。実行時に変わる中身
// （MCP の実効ツール一覧〔計画 11_mcp-server-integration §5〕、将来は登録済みのデータソースと有効・無効〔#2285 の (b)(ii)。
// 計画の答え planning#850 を待つ〕）は、gRPC 面が要求に応える時点でここから足す。
// 寄与はサービスごとに DI へ登録する（`AddIntrospectionContributor<T>()`）。1 つも無ければ静的な申告がそのまま返る。
public interface IIntrospectionContributor
{
    ValueTask<IntrospectionContribution> ContributeAsync(CancellationToken ct);
}

// 寄与の中身。`Connectors` は (b)(ii) のための器で、いまは誰も寄与しない。
public sealed record IntrospectionContribution(
    IReadOnlyList<ConnectorDto> Connectors,
    IReadOnlyList<McpToolDto> McpTools)
{
    public static IntrospectionContribution Empty { get; } = new([], []);
}

public static class IntrospectionContributions
{
    // 静的な申告に寄与を順に足す（静的な分が先・寄与は登録順）。寄与の例外はそのまま投げる（握り潰さない）。
    public static async ValueTask<ServiceIntrospectionDto> ApplyAsync(
        ServiceIntrospectionDto report, IEnumerable<IIntrospectionContributor> contributors, CancellationToken ct)
    {
        List<ConnectorDto>? connectors = null;
        List<McpToolDto>? tools = null;
        foreach (var contributor in contributors)
        {
            var contribution = await contributor.ContributeAsync(ct);
            if (contribution.Connectors.Count > 0)
                (connectors ??= [.. report.Connectors]).AddRange(contribution.Connectors);
            if (contribution.McpTools.Count > 0)
                (tools ??= [.. report.McpTools]).AddRange(contribution.McpTools);
        }
        return connectors is null && tools is null
            ? report
            : report with
            {
                Connectors = (IReadOnlyList<ConnectorDto>?)connectors ?? report.Connectors,
                McpTools = (IReadOnlyList<McpToolDto>?)tools ?? report.McpTools,
            };
    }

    // 寄与を登録する（同じ型の二重登録は 1 つにする）。
    public static IServiceCollection AddIntrospectionContributor<T>(this IServiceCollection services)
        where T : class, IIntrospectionContributor
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIntrospectionContributor, T>());
        return services;
    }
}
