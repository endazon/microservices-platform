using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.ArchiveBodyless;
using DocumentService.Features.Documents.AstStaleCopies;
using DocumentService.Features.Documents.ListPage;
using DocumentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Logging;

namespace DocumentService.Features.Documents.ArchiveAstQuotes;

// FR-06, UC-03, SC-05, [[IADR-0549]]（2026-10-17 追記 / #1974。AST#1366 の後始末）:
// **AST が 5 分ごとに保存した相場スナップショット（属性 `kind = Quote`）の文書を、既存の状態「アーカイブ」へ移す管理者だけの一回限りの口**
// （`POST /documents/archive-ast-quotes`）。
//
// AST は 2026-10-16 まで相場のスナップショットを KB へ保存していた（1 日に約 950 件）。新しい保存は止めた（AST#1366）が、旧い文書は索引に残り、
// 銘柄の検索の関連度の候補（4×TopK）を占め続ける。AST の検索の絞り込みは単値の完全一致だけで「Quote でない」を書けず、取り出した後に落としても
// 候補に入れなかったニュース・開示は戻らない。アーカイブした文書は検索・RAG から外れる（[[IADR-0551]]）。
//
// 🔴 **本文の無い文書の一括アーカイブ（`archive-bodyless`。[[IADR-0549]]）と同じ手段を使う。新しい仕組みを作らない。**
//   遷移は `Document.Archive(ChangeNote)`、下流へは単票のアーカイブと同じ発行の門 `PublishUpdatedIfIndexableAsync`、
//   量の制御は同じ駆動器の本体（`scripts/lib/admin-page-driver.js`）。違うのは母集合だけである。
// 🔴 **対象は `project = ai-stock-trading` かつ `kind = Quote`（どちらも大文字小文字を区別する完全一致）の、アーカイブ済みでない組織文書だけ**である。
//   条件は口の側で固定する（引数にしない）。他の `kind`（News・Disclosure・SupplyDemand・MacroIndicator・SourceStatus・報告書の Daily など）・
//   他の project・個人資料には触らない。
// 🔴 **冪等である。** アーカイブ済みは母集合から外れるので、2 回流しても 2 回目は何もしない。
// 🔴 **消さない（Delete しない）。** 台帳と本文は残り、文書管理画面で「アーカイブ済み」として見分けられる（戻す口は無い。[[IADR-0549]] 決定 6）。
// 🔴 **`dryRun` は必須**（省略は 400）。dry-run も同じページ送りで選び、件数と対象の文書 ID だけを返す（状態は変えない）。本文は読まない。
internal static class ArchiveAstQuotesEndpoint
{
    public const string Route = "/archive-ast-quotes";

    // 版の注記。どの版がこの操作で作られたかを台帳から引くための目印である。**変えない。**
    public const string ChangeNote = "archived: AST quote snapshot (kind=Quote) — bulk archive of AST market snapshots (#1974)";

    // AST の相場スナップショットの `kind` の値（AST の `InformationKind.Quote`）。
    public const string QuoteKind = "Quote";

    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 200;

    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost(Route, async (ArchiveAstQuotesRequest req, HttpContext http,
            DocumentDbContext db, IDocumentUpdatedPublisher bus, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (Validate(req) is { } p)
                return Results.ValidationProblem(new Dictionary<string, string[]> { [p.Key] = [p.Message] });

            var dryRun = req.DryRun!.Value;
            DocumentPageCursor? after = null;
            if (req.Cursor is not null && DocumentPageCursor.TryDecode(req.Cursor, out var decoded))
                after = decoded;
            var limit = Math.Clamp(req.Limit ?? DefaultLimit, 1, MaxLimit);

            // 母集合: アーカイブ済みでない ∩ `project = ai-stock-trading` ∩ `kind = Quote` ∩ 組織文書（個人資料は `ReadPageAsync` の述語が外す）。
            // 🔴 アーカイブ済みを外すのが冪等の根拠である。属性の一致は jsonb の値変換のため SQL へ訳せず、一覧の口と同じ関数で塊の中で当てる（IADR-0509）。
            var ledger = db.Documents.Where(d => d.Status != DocumentStatus.Archived);
            var (page, next) = await DocumentPageQuery.ReadPageAsync(ledger, QuoteFilter, limit, after, readable: null, ct);

            var logger = loggers.CreateLogger(typeof(ArchiveAstQuotesEndpoint).FullName!);
            var principal = http.User.Identity?.Name ?? "(unnamed)";
            var requestedBy = LogSanitizer.Sanitize(req.RequestedBy, ArchiveBodylessDocumentsEndpoint.MaxRequestedByLength);
            var reason = LogSanitizer.Sanitize(req.Reason, ArchiveBodylessDocumentsEndpoint.MaxReasonLength);

            var archived = 0;
            if (!dryRun && page.Count > 0)
            {
                foreach (var doc in page) doc.Archive(ChangeNote);
                // FR-06, ADR-0027 §決定, IADR-0608 (#2168): ページ全件の保存と発行を 1 トランザクションで確定する（Transactional Outbox）。
                try
                {
                    await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(bus, db, page, ct);
                }
                catch (DbUpdateException ex) when (DocumentVersionConflict.Is(ex))
                {
                    // FR-02, IADR-0637 決定 7 (#2150 段 1c): ページの確定が並行トークンに負けた。ページは 1 件も確定していない（発行も出ない）。
                    return DocumentVersionConflict.ProblemForBatch(DocumentVersionConflict.BatchPageConflictMessage);
                }

                foreach (var doc in page)
                {
                    // 🔴 監査: どの文書を、誰が、何のためにアーカイブしたか（1 件 1 行。表題・本文は出さない）。
                    // 確定の後に書く（確定しなかったアーカイブを記録しない）。
                    logger.LogInformation(
                        "Archived AST quote snapshot {DocumentId} (createdAt={CreatedAt:O}) "
                        + "by {Principal} (requestedBy {RequestedBy}; reason {Reason})",
                        doc.Id, doc.CreatedAt, principal, requestedBy, reason);
                    archived++;
                }
            }

            logger.LogInformation(
                "Archive AST quotes ({Mode}): page of {Selected}, archived {Archived}; more={More} "
                + "by {Principal} (requestedBy {RequestedBy}; reason {Reason})",
                dryRun ? "dry-run" : "apply", page.Count, archived, next is not null, principal, requestedBy, reason);

            return Results.Ok(new ArchiveAstQuotesResponse(
                DryRun: dryRun,
                Selected: page.Count,
                Archived: archived,
                Ids: [.. page.Select(d => d.Id)],
                NextCursor: next));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly)
          .WithName("ArchiveAstQuotes")
          .Produces<ArchiveAstQuotesResponse>();
    }

    private static readonly IReadOnlyDictionary<string, string> QuoteFilter = new Dictionary<string, string>
    {
        [RestrictedProject.DocumentKey] = AstStaleCopyRules.AstProject,
        [AstStaleCopyRules.KindKey] = QuoteKind,
    };

    // 要求の検証。**先頭の 1 件を、その鍵で返す**（本文の無い文書の一括アーカイブの口と同じ作法・同じ上限）。
    internal static (string Key, string Message)? Validate(ArchiveAstQuotesRequest req)
    {
        if (req.DryRun is null)
            return ("dryRun", "dryRun を明示してください（true で件数と対象の ID だけを返し、false でアーカイブします）。");
        if (req.Limit is < 1 or > MaxLimit)
            return ("limit", $"limit は 1〜{MaxLimit} で指定してください。");
        if (req.DryRun == false && string.IsNullOrWhiteSpace(req.Reason))
            return ("reason", "アーカイブする呼び出しでは reason が必須です（監査のログへ残します）。");
        if (req.RequestedBy is { Length: > ArchiveBodylessDocumentsEndpoint.MaxRequestedByLength })
            return ("requestedBy", $"requestedBy は {ArchiveBodylessDocumentsEndpoint.MaxRequestedByLength} 文字までです。");
        if (req.Reason is { Length: > ArchiveBodylessDocumentsEndpoint.MaxReasonLength })
            return ("reason", $"reason は {ArchiveBodylessDocumentsEndpoint.MaxReasonLength} 文字までです。");
        if (req.Cursor is not null && !DocumentPageCursor.TryDecode(req.Cursor, out _))
            return ("cursor", "カーソルが不正です。前の応答の nextCursor をそのまま渡してください。");
        return null;
    }
}

// FR-06, [[IADR-0549]]（#1974）: 要求。`dryRun` は必須。`limit` は既定 100・1〜200。アーカイブする呼び出し（`dryRun=false`）は `reason` が必須。
public sealed record ArchiveAstQuotesRequest(
    bool? DryRun,
    int? Limit = null,
    string? Cursor = null,
    string? RequestedBy = null,
    string? Reason = null);

// FR-06, [[IADR-0549]]（#1974）: 応答。件数はこのページについて数える。`ids` はこのページの対象（dry-run ではアーカイブする文書、本番ではアーカイブした文書）。
public sealed record ArchiveAstQuotesResponse(
    bool DryRun,
    int Selected,
    int Archived,
    IReadOnlyList<Guid> Ids,
    string? NextCursor);
