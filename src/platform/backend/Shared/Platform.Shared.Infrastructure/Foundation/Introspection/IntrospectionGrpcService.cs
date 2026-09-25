using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Dto = Platform.Shared.Contracts.Dtos;
using Pb = Platform.Shared.Contracts.Grpc.Introspection.V1;

namespace Platform.Shared.Infrastructure.Foundation.Introspection;

// FR-15, NFR-09, NFR-16, ADR-0018, ADR-0029, ADR-0075, IADR-0029, IADR-0379, IADR-0462 (#1514, #1255 経路 ⑤):
// 自己申告の **east-west gRPC 面**（`platform.introspection.v1.ServiceIntrospection/Get`）。
//
// 🔴 **本体は持たない。** DI の `ServiceIntrospectionDto`（`AddPlatformIntrospection` が組み立てた 1 つ）を
// 輸送の言葉へ写すだけであり、REST の `GET /internal/introspection` と**同じ 1 つの申告**を返す
// （申告を 2 つ持たない。片方だけ更新されて輸送ごとに違う申告が返る形を作らない）。
//
// 🔴 **ServiceCaller を要求する。** 利用者のトークンは（管理者であっても）通らない（IADR-0379 決定 4）。
// REST の受け口は認証を持たない（メッシュ内部限定）ので、この面は現状より**狭い**。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class IntrospectionGrpcService(Dto.ServiceIntrospectionDto report)
    : Pb.ServiceIntrospection.ServiceIntrospectionBase
{
    public override Task<Pb.ServiceIntrospectionReport> Get(
        Pb.GetServiceIntrospectionRequest request, ServerCallContext context)
        => Task.FromResult(IntrospectionGrpcMapping.ToProto(report));
}

// 自己申告の DTO と proto の写し（受け口と呼び出し側で同じ 1 つを使う）。
//
// 🔴 **null を取り得るのは `Target` だけ**であり、presence（`HasTarget`）で往復させる。
// `HasTarget` を読まずに素の値を読むと、未設定が `""` に化ける ——「接続先を申告しない」ポートが
// 「空文字の接続先へ繋いでいる」ポートになる（例外は 1 つも起きない）。
public static class IntrospectionGrpcMapping
{
    public static Pb.ServiceIntrospectionReport ToProto(Dto.ServiceIntrospectionDto dto)
    {
        var report = new Pb.ServiceIntrospectionReport { Service = dto.Service };
        foreach (var step in dto.Steps)
        {
            var s = new Pb.StepIntrospection
            {
                Name = step.Name,
                Consumer = step.Consumer,
                Input = step.Input,
                Enabled = step.Enabled,
            };
            s.Outputs.AddRange(step.Outputs);
            report.Steps.Add(s);
        }
        foreach (var port in dto.Ports)
        {
            var p = new Pb.PortSelection { Port = port.Port, Implementation = port.Implementation };
            if (port.Target is not null)
                p.Target = port.Target;
            report.Ports.Add(p);
        }
        foreach (var connector in dto.Connectors)
            report.Connectors.Add(new Pb.Connector { Name = connector.Name, Enabled = connector.Enabled });
        return report;
    }

    public static Dto.ServiceIntrospectionDto ToDto(Pb.ServiceIntrospectionReport report) =>
        new(
            report.Service,
            report.Steps
                .Select(s => new Dto.StepIntrospectionDto(s.Name, s.Consumer, s.Input, s.Outputs.ToList(), s.Enabled))
                .ToList(),
            report.Ports
                .Select(p => new Dto.PortSelectionDto(p.Port, p.Implementation, p.HasTarget ? p.Target : null))
                .ToList(),
            report.Connectors
                .Select(c => new Dto.ConnectorDto(c.Name, c.Enabled))
                .ToList());
}
