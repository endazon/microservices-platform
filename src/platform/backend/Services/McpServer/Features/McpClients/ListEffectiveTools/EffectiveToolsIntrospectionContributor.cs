using McpServer.Domain;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;

namespace McpServer.Features.McpClients.ListEffectiveTools;

// FR-15, FR-16, ADR-0024 §5, IADR-0660 (#2285): **実効ツール一覧を構成情報 API へ申告する**寄与（計画 11_mcp-server-integration §5
// 「実効ツール一覧は構成情報 API（FR-15）へ申告し、SC-11 構成ビューアで可視化する」）。
// 自己申告の gRPC 面が要求に応えるたびに、カタログ（`ToolCatalogRefresher` が周期的に申告と公開構成を突合して更新する）の
// その時点の実効ツールを、公開名の順に載せる。入力スキーマと説明は載せない（構成の可視化に要らない）。
public sealed class EffectiveToolsIntrospectionContributor(ToolCatalog catalog) : IIntrospectionContributor
{
    public ValueTask<IntrospectionContribution> ContributeAsync(CancellationToken ct) =>
        ValueTask.FromResult(new IntrospectionContribution(
            [],
            [.. catalog.PublishedTools
                .OrderBy(t => t.PublishedName, StringComparer.Ordinal)
                .Select(t => new McpToolDto(
                    t.PublishedName, t.Service, t.Declaration.RequiredScope, t.Declaration.EgressClass))]));
}
