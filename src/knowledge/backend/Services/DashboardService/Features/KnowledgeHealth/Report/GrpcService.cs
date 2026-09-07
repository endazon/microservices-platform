using Grpc.Core;
using Knowledge.Contracts.Grpc.Dashboard.V1;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Kernel;

namespace DashboardService.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-18, FR-19, NFR-09, NFR-16, UC-05, SC-10, ADR-0002, ADR-0006, ADR-0029, ADR-0030,
// ADR-0065, ADR-0075, [[IADR-0265]], [[IADR-0299]], [[IADR-0353]], [[IADR-0379]], [[IADR-0389]],
// [[IADR-0402]], [[IADR-0408]] (#1255): ナレッジ健全性の観測値の受け口の **east-west gRPC 面**。
//
// 🔴 **本体は持たない。** `ReportKnowledgeHealthUseCase` を呼ぶだけであり、REST の
// `POST /internal/knowledge-health/observations` と**同じ関数**を通る（判定器を 2 つにしない。
// [[IADR-0397]] / [[IADR-0400]] / [[IADR-0402]] と同じ形）。ここに在るのは
// 「輸送の言葉へ写すこと」だけである。
//
// 🔴 **ServiceCaller を要求する。** 利用者のトークンは（管理者であっても）通らない ——
//   通すと呼び出し先が「利用者が直接呼んだ」と区別できず confused deputy が成立する
//   （[[IADR-0379]] 決定 4）。これを機械で守るのは
//   `GrpcKnowledgeHealthReportTests.Report_with_forwarded_admin_user_token_is_permission_denied`
//   （管理者トークンでも PERMISSION_DENIED）である。
//   REST 側の受け口は**認証を持たない**（[[IADR-0299]] 決定 4・利用者裁定。生産者は利用者 JWT を
//   持たない定期処理である）ので、この面は現状より**狭い**
//   （[[IADR-0401]] 決定 1 / [[IADR-0402]] 決定 3 と同じ向き）。**REST 側は変えない。**
//
// 🔴 **個人資料の除外はここでも行わない。** 集計から `private-note` を外すのは閲覧側であり
//   （[[IADR-0265]]）、受け口は観測値をそのまま保存する。**移行で判定の位置を動かさない。**
//
// 🔴 **status への写像は REST の状態コードと 1:1 である。**
//   `ErrorKind.Validation` → `INVALID_ARGUMENT`（REST の 400）、それ以外 → `INTERNAL`（REST の 500）。
//   **成功以外を成功に見せない**（[[IADR-0256]] 決定 3）—— 呼び出し元は `RecordDelivered` を
//   「受理されたときだけ」数えており、ここで握ると沈黙が鳴らなくなる。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class KnowledgeHealthReportGrpcService(ReportKnowledgeHealthUseCase reports)
    : KnowledgeHealthReport.KnowledgeHealthReportBase
{
    public override async Task<ReportResponse> Report(ReportRequest request, ServerCallContext context)
    {
        var outcome = await reports.ExecuteAsync(ToCommand(request), context.CancellationToken);
        if (outcome.IsFailure)
        {
            var status = outcome.Error.Kind == ErrorKind.Validation
                ? StatusCode.InvalidArgument
                : StatusCode.Internal;
            throw new RpcException(new Status(status, outcome.Error.Message));
        }

        return new ReportResponse
        {
            Indicator = outcome.Value.Indicator,
            Accepted = outcome.Value.Accepted,
        };
    }

    // 🔴 **proto3 の「未指定」を REST の null へ戻す写し**（[[IADR-0408]] 決定 3）。
    // `Has*` を読まずに素の値を読むと、未指定が `""` / `0` になって別の事実に化ける:
    //   - `ThresholdDays` … `0` は検証器が弾く（`thresholdDays must be greater than zero`）ので、
    //     **しきい値を持たない 3 指標の報告が全部 400 になる**。
    //   - `DocScope` … `""` は「個人資料ではない」（null）と台帳で区別できない。
    //   - `Dimension` … `""` という軸が 1 本生まれ、内訳の集計が割れる。
    internal static KnowledgeHealthReportRequest ToCommand(ReportRequest request) => new(
        request.Indicator,
        request.Observations
            .Select(o => new KnowledgeHealthObservationRequest(
                o.SubjectKey,
                o.HasDocScope ? o.DocScope : null,
                o.HasDimension ? o.Dimension : null))
            .ToList(),
        request.HasThresholdDays ? request.ThresholdDays : null);
}
