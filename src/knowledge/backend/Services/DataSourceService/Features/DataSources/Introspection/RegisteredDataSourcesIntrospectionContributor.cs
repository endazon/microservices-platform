using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Introspection;

namespace DataSourceService.Features.DataSources.Introspection;

// FR-15, FR-01, SC-06, SC-11, ADR-0018, IADR-0666 (#2285 の (b)(ii)): **SC-06 に登録したデータソースと有効・無効**を構成取得 API へ申告する寄与
// （計画 10_composability §6 の「コネクタ一覧」。planning#850 の裁定 (ii)・利用者裁定 2026-10-30）。
// 自己申告の gRPC 面が要求に応えるたびに DB を読む（登録・無効化は次の要求に反映される）。状態に依らず全行を、名前→ID の順に載せる。
// 🔴 ID・名前・種別・有効だけを出す。接続先・設定・資格情報は出さない（SC-06 が正本）。
// 🔴 DB の例外は握り潰さない（IADR-0660）。gRPC の失敗になり、BFF はこのサービスを「到達不能」にする —— 空を「データソースが無い」と読ませない。
public sealed class RegisteredDataSourcesIntrospectionContributor(IServiceScopeFactory scopes) : IIntrospectionContributor
{
    public async ValueTask<IntrospectionContribution> ContributeAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        var rows = await db.DataSources.AsNoTracking()
            .Select(d => new { d.Id, d.Name, d.SourceType, d.Status })
            .ToListAsync(ct);
        return new IntrospectionContribution(
            [],
            [],
            [.. rows
                .Select(r => new DataSourceSummaryDto(r.Id.ToString("D"), r.Name, r.SourceType, r.Status == DataSourceStatus.Active))
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .ThenBy(d => d.Id, StringComparer.Ordinal)]);
    }
}
