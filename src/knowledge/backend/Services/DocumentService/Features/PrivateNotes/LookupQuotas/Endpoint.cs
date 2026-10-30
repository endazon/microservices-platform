using DocumentService.Domain;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Features.PrivateNotes.LookupQuotas;

// FR-19, NFR-27, SC-17 (#2348): システム管理者による容量の一括照会（SC-17 の一覧の N+1 を解く）。
// 使用量は所有者ごとの合計を 1 回の集計で、上限は該当の行を 1 回の照会で引く（利用者数に比例する往復をしない）。
// 値の定義は 1 件の口（GetQuota）と同じ: 使用量＝最新版と論理削除済みの合計、上限の行が無ければ既定の 1 GB。
// 🔴 返すのは利用者ごとの 2 つの数（と割合）だけ。読み取りだが POST にする（利用者名の列を URL に載せると要求行の上限に当たり得る）。
internal static class LookupPrivateNoteQuotasEndpoint
{
    internal const int MaxOwners = 1000;
    internal const int MaxOwnerIdLength = 256;

    internal static void Map(RouteGroupBuilder admin)
    {
        admin.MapPost("/lookup", async (PrivateNoteQuotaLookupRequest req, DocumentDbContext db,
            CancellationToken ct) =>
        {
            // 本文に ownerIds が無ければ逆直列化は null を入れる（契約は必須。ここで 400 に倒す）。
            IReadOnlyList<string> ownerIds = req.OwnerIds is { } given ? given : [];
            if (ownerIds.Count == 0 || ownerIds.Count > MaxOwners
                || ownerIds.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > MaxOwnerIdLength))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ownerIds"] = [$"1〜{MaxOwners} 件の、空白でない {MaxOwnerIdLength} 文字以下の利用者名を指定してください。"],
                });
            }

            var distinct = ownerIds.Distinct(StringComparer.Ordinal).ToList();
            var used = await db.PrivateNotes
                .Where(n => distinct.Contains(n.OwnerId))
                .GroupBy(n => n.OwnerId)
                .Select(g => new { OwnerId = g.Key, Used = g.Sum(n => n.LatestBytes) })
                .ToDictionaryAsync(x => x.OwnerId, x => x.Used, ct);
            var limits = await db.PrivateNoteQuotas
                .Where(q => distinct.Contains(q.OwnerId))
                .ToDictionaryAsync(q => q.OwnerId, q => q.LimitBytes, ct);

            var items = distinct.Select(owner =>
            {
                var u = used.GetValueOrDefault(owner);
                var limit = limits.TryGetValue(owner, out var l) ? l : PrivateNoteQuota.DefaultLimitBytes;
                return new PrivateNoteOwnerUsageDto(owner, u, limit, limit <= 0 ? 100 : (int)(u * 100 / limit));
            }).ToList();
            return Results.Ok(new PrivateNoteQuotaLookupResponse(items));
        });
    }
}
