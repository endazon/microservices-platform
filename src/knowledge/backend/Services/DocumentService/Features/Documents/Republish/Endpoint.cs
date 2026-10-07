using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.ListPage;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Logging;

namespace DocumentService.Features.Documents.Republish;

// FR-02, FR-06, UC-04, ADR-0013, ADR-0027, [[IADR-0503]] (#1762):
// **全文書（または条件で絞った文書）へ `DocumentUpdated` を再発行する、管理者だけの口**（`POST /documents/republish-updated`）。
//
// 射影（Qdrant の索引・Wiki.js・グラフ）は台帳の写しであり、作り直す手段は `DocumentUpdated` の再発行である
// （運用仕様書の再索引の手順 2）。その手段がリポジトリに無かった（#1762）。台帳を持つのはこのサービスだけなので、ここに置く。
//
// 🔴 **発行は既存の門（`DocumentEndpoints.PublishUpdatedIfIndexableAsync`）を通す。** 中身（共有先・タグの表示名・本文指紋・
//   本文の有無・原本の所在）は通常の経路と同じ関数で載り、3 トグルとも OFF の個人資料は出ない（`PublishGateCoverageTests`）。
// 🔴 **`AdminOnly` を外さない。** 1 回の呼び出しで最大 500 件の発行を起こし、埋め込みの費用（Voyage）と取り込み・Wiki 同期の負荷を生む。
//   群（`write`）は運用者と機械の書き手（AST の KB 書き込み）にも開いているので、ここで積まないと彼らが全件の再発行を起こせる。
// 🔴 **`dryRun` は必須**（省略は 400）。確かめるつもりの呼び出しが発行に倒れないようにする。
//
// 量の制御（ページの大きさ・間隔・取り込みのキューの深さ・DLQ の監視）と中断・再開は駆動スクリプト
// （`scripts/republish-document-updated.js`）が持つ。口が持つのは「1 ページを選んで発行する」ことと、続きのカーソルだけである。
//
// ［2026-10-08 / #1765 / [[IADR-0509]]］**1 ページで読むのは `limit + 1` 行**（属性の絞り込みが無いとき）。キーセットは SQL で
// `(CreatedAt, Id)` の索引を引き、件数（`matched` / `remaining`）は `COUNT(*)` で取る。従前はページごとに絞り込みの全件の
// 投影を読んでいた（1 ページ O(N)、1 回の走査 O(N²)）。属性の絞り込みがあるときは従前の経路のまま（決定 3）。
internal static class RepublishDocumentUpdatedEndpoint
{
    public const string Route = "/republish-updated";

    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost(Route, async (RepublishDocumentUpdatedRequest req, HttpContext http,
            DocumentDbContext db, IDocumentUpdatedPublisher bus, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var problem = RepublishSelection.Validate(req);
            if (problem is { } p)
                return Results.ValidationProblem(new Dictionary<string, string[]> { [p.Key] = [p.Message] });

            DocumentPageCursor? after = null;
            if (req.Cursor is not null && DocumentPageCursor.TryDecode(req.Cursor, out var decoded))
                after = decoded;

            // 絞り込みに要る列だけを投影して読む（22,564 件の本体をページごとに読まない）。
            // `createdBefore` と `ids` は SQL 側で絞る。
            // 🔴 `createdBefore` は UTC へ寄せて渡す —— Npgsql は offset 0 の `DateTimeOffset` しか timestamptz へ書けない。
            var query = db.Documents.AsNoTracking();
            if (req.CreatedBefore is { } before)
            {
                var beforeUtc = before.ToUniversalTime();
                query = query.Where(d => d.CreatedAt < beforeUtc);
            }
            if (req.Ids is { Count: > 0 } ids)
                query = query.Where(d => ids.Contains(d.Id));

            var dryRun = req.DryRun == true;
            var limit = RepublishSelection.ClampLimit(req.Limit);
            int matched;
            int remainingCount;
            List<RepublishSelection.Row> selected;
            string? next = null;
            if (req.Attributes is { Count: > 0 })
            {
                // 属性の絞り込みがあるときは従前の経路（全件の投影をメモリで絞って並べる。1 ページ O(N)）。
                // 属性は jsonb の値変換で SQL へ訳せず、`matched` / `remaining` を正しく数えるには全件の属性を見るしかない
                // （[[IADR-0509]] 決定 3。全件の再索引＝属性なしの経路が主な用途である）。
                var ordered = RepublishSelection.Order(await ProjectAsync(query, ct), req.Attributes);
                var remaining = RepublishSelection.Remaining(ordered, after);
                matched = ordered.Count;
                remainingCount = remaining.Count;
                if (dryRun) selected = remaining;
                else (selected, next) = RepublishSelection.Slice(remaining, limit);
            }
            else
            {
                // FR-02, FR-06, [[IADR-0509]] (#1765): **キーセットを SQL で行う**（`(CreatedAt, Id)` の索引で引く）。
                // 1 ページで読む行は `limit + 1` 件（続きの有無を知るため）。件数は `COUNT(*)` で別に取る（行は運ばない）。
                // 🔴 並べる・カーソルと比べるは**どちらも SQL**（`AfterCursor` / `InPageOrder`）。.NET 側で並べ直さない。
                matched = await query.CountAsync(ct);
                var keyed = query.AfterCursor(after);
                if (dryRun)
                {
                    // dry-run は残り全件の内訳を数える（従前どおり O(N)。走査の前に 1 回だけ呼ぶ）。
                    selected = await ProjectAsync(keyed.InPageOrder(), ct);
                    remainingCount = selected.Count;
                }
                else
                {
                    remainingCount = after is null ? matched : await keyed.CountAsync(ct);
                    var head = await ProjectAsync(keyed.InPageOrder().Take(limit + 1), ct);
                    (selected, next) = RepublishSelection.Slice(head, limit);
                }
            }

            var logger = loggers.CreateLogger(typeof(RepublishDocumentUpdatedEndpoint).FullName!);
            var principal = http.User.Identity?.Name ?? "(unnamed)";
            var requestedBy = LogSanitizer.Sanitize(req.RequestedBy, RepublishSelection.MaxRequestedByLength);
            var reason = LogSanitizer.Sanitize(req.Reason, RepublishSelection.MaxReasonLength);

            if (dryRun)
            {
                // 🔴 **dry-run も記録する**（誰がいつ全件の内訳を引いたか。発行の前段であり、監査で追えるようにする）。
                var summary = RepublishSelection.Summarize(selected);
                logger.LogInformation(
                    "Republish dry-run: {Remaining} of {Matched} document(s) remaining (skipped by the publish gate {Skipped}; "
                    + "without body {WithoutBody}) by {Principal} (requestedBy {RequestedBy}; reason {Reason})",
                    remainingCount, matched, summary.SkippedByGate, summary.WithoutBody, principal, requestedBy, reason);
                return Results.Ok(new RepublishDocumentUpdatedResponse(
                    DryRun: true,
                    Matched: matched,
                    Remaining: remainingCount,
                    Selected: selected.Count,
                    Published: 0,
                    SkippedByGate: summary.SkippedByGate,
                    WithoutBody: summary.WithoutBody,
                    ByConfidentiality: summary.ByConfidentiality,
                    NextCursor: null));
            }

            var page = selected;

            // ページの文書だけを本体ごと読み直し、選んだ並びのまま発行する。
            // 投影から読み直しまでの間に消えた文書は飛ばす（削除の経路が `DocumentDeleted` を出している）。
            var pageIds = page.Select(r => r.Id).ToList();
            var documents = await db.Documents.Where(d => pageIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
            var names = await TagResolver.NamesAsync(db);

            var published = 0;
            var skippedByGate = 0;
            foreach (var row in page)
            {
                if (!documents.TryGetValue(row.Id, out var doc)) continue;
                if (!DocumentEndpoints.PassesPublishGate(doc))
                {
                    skippedByGate++;
                    continue;
                }
                await DocumentEndpoints.PublishUpdatedIfIndexableAsync(bus, db, doc, names, ct);
                published++;
            }

            logger.LogInformation(
                "Republished DocumentUpdated for {Published} document(s) (skipped by the publish gate {Skipped}; "
                + "remaining before this page {Remaining} of {Matched}; more={More}) by {Principal} (requestedBy {RequestedBy}; reason {Reason})",
                published, skippedByGate, remainingCount, matched, next is not null, principal, requestedBy, reason);
            var pageSummary = RepublishSelection.Summarize(page);

            return Results.Ok(new RepublishDocumentUpdatedResponse(
                DryRun: false,
                Matched: matched,
                Remaining: remainingCount,
                Selected: page.Count,
                Published: published,
                SkippedByGate: skippedByGate,
                WithoutBody: pageSummary.WithoutBody,
                ByConfidentiality: pageSummary.ByConfidentiality,
                NextCursor: next));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly)
          .WithName("RepublishDocumentUpdated")
          .Produces<RepublishDocumentUpdatedResponse>();
    }

    // 絞り込みと内訳に要る列だけを投影して読む（並びは渡された問い合わせのまま）。
    private static async Task<List<RepublishSelection.Row>> ProjectAsync(IQueryable<Document> query, CancellationToken ct)
        => [.. (await query
                .Select(d => new { d.Id, d.CreatedAt, d.Attributes, HasMarkdownUri = d.MarkdownUri != null })
                .ToListAsync(ct))
            .Select(r => new RepublishSelection.Row(r.Id, r.CreatedAt, r.Attributes, r.HasMarkdownUri))];
}
