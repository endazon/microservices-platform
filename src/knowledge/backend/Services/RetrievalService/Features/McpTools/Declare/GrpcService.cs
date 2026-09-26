using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Pb = Platform.Shared.Contracts.Grpc.Mcp.V1;

namespace RetrievalService.Features.McpTools.Declare;

// FR-16, NFR-09, NFR-16, ADR-0024 §2, ADR-0029, ADR-0075, [[IADR-0379]] 決定 4・5, [[IADR-0462]]
// （2026-09-26 追記 / #1515, #1255 経路 ④-a）: ツール定義の自己申告の **east-west gRPC 面**
// （`platform.mcp.v1.McpToolDeclarations/Declare`）。
//
// 🔴 **本体は持たない。** REST `GET /internal/mcp-tools` と**同じ関数**（`McpToolDeclarationSource.Declare`）を
// 呼んで輸送の言葉へ写すだけであり、申告を 2 つ持たない（片方だけ更新されて輸送ごとに違う申告が返る形を作らない）。
// 個人資料の除外（`Publishable`）も同じ 1 本の経路を通る。
//
// 🔴 **ServiceCaller を要求する。** 利用者のトークンは（管理者であっても）通らない（[[IADR-0379]] 決定 4）。
// REST の受け口は認証を持たない（メッシュ内部限定）ので、この面は現状より**狭い**。REST 側は変えない。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class McpToolDeclarationGrpcService(IConfiguration configuration)
    : Pb.McpToolDeclarations.McpToolDeclarationsBase
{
    public override Task<Pb.ServiceToolDeclarations> Declare(
        Pb.DeclareMcpToolsRequest request, ServerCallContext context)
        => Task.FromResult(ToProto(McpToolDeclarationSource.Declare(configuration)));

    // REST の DTO（McpServer の `Domain/McpToolContracts.cs` の写し）と proto は 6 項目が 1 対 1。
    private static Pb.ServiceToolDeclarations ToProto(ServiceToolDeclarations declared)
    {
        var message = new Pb.ServiceToolDeclarations { Service = declared.Service };
        foreach (var tool in declared.Tools)
        {
            message.Tools.Add(new Pb.McpToolDeclaration
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.InputSchema,
                Endpoint = tool.Endpoint,
                RequiredScope = tool.RequiredScope,
                EgressClass = tool.EgressClass,
            });
        }
        return message;
    }
}
