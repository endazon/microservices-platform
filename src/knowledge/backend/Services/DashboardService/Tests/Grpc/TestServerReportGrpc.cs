using DashboardService.Features.KnowledgeHealth.Report;
using Grpc.Core;
using Grpc.Net.Client;
using Pb = Knowledge.Contracts.Grpc.Dashboard.V1;

namespace DashboardService.Tests.Grpc;

// FR-10, FR-17, FR-18, NFR-16, ADR-0089 決定 1, [[IADR-0533]] (#1902): 観測値の REST の受け口
// （`POST /internal/knowledge-health/observations`）を撤去したので、観測値の投入は器（`TestWebApplicationFactory` の
// TestServer）の上で **gRPC 面**（`KnowledgeHealthReport/Report`）を叩く。
//
// 🔴 **gRPC 面は `ServiceCaller`（`platform-service`）を要する。** 器の既定の主体は `platform-admin` なので、
// 呼び出しごとにロールのヘッダを `platform-service` へ差し替える（閲覧の GET は同じ HttpClient のまま既定の主体で行う）。
internal static class TestServerReportGrpc
{
    /// <summary>gRPC の結末。<see cref="Response"/> は <see cref="Status"/> が OK のときだけ在る。</summary>
    public sealed record Result(StatusCode Status, Pb.ReportResponse? Response, string? Detail = null);

    public static Task<Result> ReportAsync(
        this HttpClient client, KnowledgeHealthReportRequest request, CancellationToken ct)
        => client.ReportAsync(ToProto(request), ct);

    public static async Task<Result> ReportAsync(
        this HttpClient client, Pb.ReportRequest request, CancellationToken ct)
    {
        using var channel = GrpcChannel.ForAddress(
            client.BaseAddress!, new GrpcChannelOptions { HttpClient = client });
        try
        {
            var resp = await new Pb.KnowledgeHealthReport.KnowledgeHealthReportClient(channel).ReportAsync(
                request, headers: new Metadata { { TestAuthHandler.RolesHeader, "platform-service" } },
                cancellationToken: ct);
            return new Result(StatusCode.OK, resp);
        }
        catch (RpcException ex)
        {
            return new Result(ex.StatusCode, null, ex.Status.Detail);
        }
    }

    // 生産者（graph の `GrpcKnowledgeHealthReporter`）と同じ写し: null は「未指定」（has なし）のまま送る。
    public static Pb.ReportRequest ToProto(KnowledgeHealthReportRequest request)
    {
        var proto = new Pb.ReportRequest { Indicator = request.Indicator };
        if (request.ThresholdDays is { } days) proto.ThresholdDays = days;
        foreach (var o in request.Observations)
        {
            var observation = new Pb.Observation { SubjectKey = o.SubjectKey };
            if (o.DocScope is not null) observation.DocScope = o.DocScope;
            if (o.Dimension is not null) observation.Dimension = o.Dimension;
            proto.Observations.Add(observation);
        }
        return proto;
    }
}
