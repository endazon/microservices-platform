using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace DocumentService.Features.Documents.AstStaleCopies;

// FR-06, FR-05, SC-05, NFR-09, 計画 ADR-0122 決定 1・2・3・4, ADR-0121 決定 3・4（段 2）, [[IADR-0484]] (#1667):
// **AST の古い写しを列挙する、管理者だけが使える読み取り専用の口**（`GET /documents/ast-stale-copies`）。
//
// 内容の ABAC を有効にする前に、基盤の管理者がこの結果を確かめてから SC-05 の管理者の経路で消す（ADR-0121 決定 3）。
// 切替（DB の破棄）の後は、この口で対象が 0 件であることを確かめる（ADR-0122 決定 3）。
//
// 🔴 **書き込まない。削除の口を足さない。** 削除は稼働クラスタで管理者が行い、伝播は既存の削除の口（ADR-0057）に委ねる。
// 🔴 **`AdminOnly` を外さない。** 応答は個人資料以外の全文書を理由ごとに数え、対象の表題を返す。群（`read`）は認証だけなので、
//   ここで積まないと認証済みの誰もが台帳全体の内訳を引ける。
//
// 見分けの規則は `AstStaleCopyRules`（純粋関数）。ここは台帳を 1 回引き、規則に掛けて集計するだけである。
internal static class ListAstStaleCopiesEndpoint
{
    public const string Route = "/ast-stale-copies";

    internal static void Map(RouteGroupBuilder read)
    {
        read.MapGet(Route, async (DocumentDbContext db, CancellationToken ct) =>
        {
            // 最初の版の ChangeNote（作成の経路）を投影する。本文・版の全件は読まない。
            var rows = await db.Documents.AsNoTracking()
                .Select(d => new
                {
                    d.Id,
                    d.Title,
                    d.Status,
                    d.Attributes,
                    d.CreatedAt,
                    d.UpdatedAt,
                    FirstChangeNote = d.Versions.OrderBy(v => v.Version).Select(v => v.ChangeNote).FirstOrDefault(),
                })
                .ToListAsync(ct);

            var excluded = AstStaleCopyRules.Reasons.All.ToDictionary(r => r, _ => 0, StringComparer.Ordinal);
            var items = new List<AstStaleCopyItem>();
            var currentAccountReportKeys = new Dictionary<(string Kind, string PeriodKey), int>();

            foreach (var row in rows)
            {
                var verdict = AstStaleCopyRules.Classify(row.Title, row.Attributes, row.FirstChangeNote);
                if (!verdict.IsTarget)
                {
                    excluded[verdict.ExcludedReason!]++;
                    if (verdict is { ExcludedReason: AstStaleCopyRules.Reasons.OwnedByCurrentAccount, Category: AstCopyCategory.Report })
                    {
                        // 形の判定（`IsReport`）を通っているので 2 つとも空白でない値を持つ。読み方は規則と同じ `ValueOrNull` にそろえる。
                        var key = (AstStaleCopyRules.ValueOrNull(row.Attributes, AstStaleCopyRules.KindKey)!,
                            AstStaleCopyRules.ValueOrNull(row.Attributes, AstStaleCopyRules.PeriodKeyKey)!);
                        currentAccountReportKeys[key] = currentAccountReportKeys.GetValueOrDefault(key) + 1;
                    }
                    continue;
                }

                var isReport = verdict.Category == AstCopyCategory.Report;
                items.Add(new AstStaleCopyItem(
                    row.Id,
                    row.Title,
                    isReport ? "report" : "article",
                    verdict.OwnerState == AstCopyOwnerState.System ? AstStaleCopyRules.ReservedOwner : "missing",
                    AstStaleCopyRules.ValueOrNull(row.Attributes, Platform.Shared.Contracts.Dtos.RestrictedProject.DocumentKey) is not null,
                    row.Status,
                    row.CreatedAt,
                    row.UpdatedAt,
                    AstStaleCopyRules.ValueOrNull(row.Attributes, AstStaleCopyRules.KindKey),
                    isReport ? AstStaleCopyRules.ValueOrNull(row.Attributes, AstStaleCopyRules.PeriodKeyKey) : null,
                    isReport ? null : AstStaleCopyRules.PublishedAt(row.Attributes)));
            }

            items.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt) is var c and not 0 ? c : a.Id.CompareTo(b.Id));
            var reports = items.Where(i => i.Category == "report").ToList();
            var articles = items.Where(i => i.Category == "article").ToList();

            return Results.Ok(new AstStaleCopiesResponse(
                Scanned: rows.Count,
                Targets: new AstStaleCopyTargets(
                    items.Count,
                    new AstStaleReportSummary(reports.Count, MinOrNull(reports, i => i.CreatedAt), MaxOrNull(reports, i => i.CreatedAt)),
                    new AstStaleArticleSummary(articles.Count,
                        MinOrNull(articles, i => i.CreatedAt), MaxOrNull(articles, i => i.CreatedAt),
                        articles.Select(i => i.PublishedAt).Where(p => p is not null).Min(),
                        articles.Select(i => i.PublishedAt).Where(p => p is not null).Max())),
                Excluded: excluded,
                CurrentAccountReports: new AstCurrentAccountReports(
                    currentAccountReportKeys.Values.Sum(),
                    [.. currentAccountReportKeys
                        .Where(kv => kv.Value > 1)
                        .OrderBy(kv => kv.Key.Kind, StringComparer.Ordinal)
                        .ThenBy(kv => kv.Key.PeriodKey, StringComparer.Ordinal)
                        .Select(kv => new AstDuplicatedReport(kv.Key.Kind, kv.Key.PeriodKey, kv.Value))]),
                Items: items));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);
    }

    private static DateTimeOffset? MinOrNull(List<AstStaleCopyItem> items, Func<AstStaleCopyItem, DateTimeOffset> at)
        => items.Count == 0 ? null : items.Min(at);

    private static DateTimeOffset? MaxOrNull(List<AstStaleCopyItem> items, Func<AstStaleCopyItem, DateTimeOffset> at)
        => items.Count == 0 ? null : items.Max(at);
}

// 応答。`Scanned` ＝ `Targets.Total` ＋ `Excluded` の合計。`Excluded` は理由をすべて並べる（0 件も出す）。
public sealed record AstStaleCopiesResponse(
    int Scanned,
    AstStaleCopyTargets Targets,
    IReadOnlyDictionary<string, int> Excluded,
    AstCurrentAccountReports CurrentAccountReports,
    IReadOnlyList<AstStaleCopyItem> Items);

public sealed record AstStaleCopyTargets(int Total, AstStaleReportSummary Reports, AstStaleArticleSummary Articles);

// 期間は件数 0 なら null。
public sealed record AstStaleReportSummary(int Count, DateTimeOffset? CreatedFrom, DateTimeOffset? CreatedTo);

public sealed record AstStaleArticleSummary(
    int Count,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    DateTimeOffset? PublishedFrom,
    DateTimeOffset? PublishedTo);

// 入れ直しの後の確認用: 現在のサービスアカウントが所有する報告書の写しの件数と、同じ kind・periodKey が 2 件以上ある組。
public sealed record AstCurrentAccountReports(int Count, IReadOnlyList<AstDuplicatedReport> Duplicates);

public sealed record AstDuplicatedReport(string Kind, string PeriodKey, int Copies);

// `Owner` は `missing` か `system`。`Category` は `report` か `article`。
public sealed record AstStaleCopyItem(
    Guid Id,
    string Title,
    string Category,
    string Owner,
    bool HasProject,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Kind,
    string? PeriodKey,
    DateTimeOffset? PublishedAt);
