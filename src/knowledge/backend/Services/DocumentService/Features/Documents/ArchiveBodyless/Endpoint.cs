using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.ListPage;
using DocumentService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Logging;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.Documents.ArchiveBodyless;

// FR-06, UC-03, SC-05, ADR-0106, [[IADR-0549]] (#1931 項目 3。オーナー裁定 B・2026-10-15):
// **本文のオブジェクトが無い（NoSuchKey）古い文書をアーカイブする、管理者だけの一回限りの口**（`POST /documents/archive-bodyless`）。
//
// 9/26 の SeaweedFS への切り替えで旧ストレージの中身は破棄された（裁定済み）。それより前に作られた文書は台帳に残るが本文が無い。
// その文書を、既存の状態「アーカイブ」（SC-05 の `lifecycle = archived`）へ移す。状態を新設しない。
//
// 🔴 **対象は「`createdAt < createdBefore`」かつ「アーカイブ済みでない」かつ「本文の参照が storage:// である」かつ
//   「そのオブジェクトが NoSuchKey」の組織文書だけである。** どれか 1 つでも外れる文書には触らない。
//   - `createdBefore` は**必須**（既定を持たない）。切り替え後の文書を巻き込む呼び出しを組み立てられないようにする。
//   - 本文の有無はオブジェクトストレージへの存在確認（HEAD 相当。`IObjectStorageClient.ExistsAsync`）で決める。本文を読まない。
//     「無い」は NoSuchKey だけ（[[IADR-0540]] 決定 2 と同じ判定）。バケットが無い・時間切れ・5xx は例外のまま上がり、
//     **そのページの文書は 1 件もアーカイブしない**（保存はページの確かめが全部終わってから 1 回だけ行う）。
//   - 個人資料は対象外（単票のアーカイブの口と同じ。`DocumentManageScope` の注記）。数えるだけで触らない。
// 🔴 **冪等である。** アーカイブ済みは母集合から外れるので、2 回流しても 2 回目は何もしない。
// 🔴 **アーカイブは単票の口と同じ経路で下流へ伝える**（`Document.Archive` → 保存 → 発行の門 `PublishUpdatedIfIndexableAsync`）。
//   取り込みは全コレクションから当該文書の点を消し（[[IADR-0540]] の冒頭の削除）、Wiki 同期はページを非公開化する。
// 🔴 **`dryRun` は必須**（省略は 400）。dry-run も同じページ送りで存在を確かめ、件数と内訳だけを返す（状態は変えない）。
//
// 量の制御（ページの大きさ・カナリア・キューと DLQ の監視）と中断・再開は駆動スクリプト
// （`scripts/archive-bodyless-documents.js`）が持つ。口が持つのは「1 ページを確かめてアーカイブする」ことと、続きのカーソルだけである。
internal static class ArchiveBodylessDocumentsEndpoint
{
    public const string Route = "/archive-bodyless";

    // 版の注記（`DocumentVersion.ChangeNote`）。どの版がこの操作で作られたかを台帳から引くための目印である
    // （戻し方の手順〔IADR-0549 決定 6〕がこの文字列で引く）。**変えない。**
    public const string ChangeNote = "archived: body object missing (NoSuchKey) — bulk archive of pre-cutover documents (#1931)";

    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 200;
    internal const int MaxRequestedByLength = 100;
    internal const int MaxReasonLength = 500;

    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost(Route, async (ArchiveBodylessDocumentsRequest req, HttpContext http,
            DocumentDbContext db, IObjectStorageClient storage, IDocumentUpdatedPublisher bus,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (Validate(req) is { } p)
                return Results.ValidationProblem(new Dictionary<string, string[]> { [p.Key] = [p.Message] });

            var dryRun = req.DryRun!.Value;
            // 🔴 UTC へ寄せて渡す —— Npgsql は offset 0 の `DateTimeOffset` しか timestamptz へ書けない。
            var before = req.CreatedBefore!.Value.ToUniversalTime();
            DocumentPageCursor? after = null;
            if (req.Cursor is not null && DocumentPageCursor.TryDecode(req.Cursor, out var decoded))
                after = decoded;

            var older = db.Documents.Where(d => d.CreatedAt < before);
            var candidates = Candidates(older);

            var matched = await candidates.CountAsync(ct);
            var alreadyArchived = await older.CountAsync(d => d.Status == DocumentStatus.Archived, ct);
            var notStored = await older.CountAsync(d => d.Status != DocumentStatus.Archived
                && (d.MarkdownUri == null || !d.MarkdownUri.StartsWith(StorageUriPrefix)), ct);

            var keyed = candidates.AfterCursor(after);
            var remaining = after is null ? matched : await keyed.CountAsync(ct);
            var limit = Math.Clamp(req.Limit ?? DefaultLimit, 1, MaxLimit);
            var head = await keyed.InPageOrder().Take(limit + 1).ToListAsync(ct);
            var page = head.Take(limit).ToList();
            var next = head.Count > limit ? DocumentPageCursor.After(page[^1]).Encode() : null;

            // ページの文書を 1 件ずつ確かめる。🔴 保存はすべて確かめ終えてから —— 途中で確かめに失敗したら何も書かない。
            var missing = new List<Document>();
            var bodyPresent = 0;
            var skippedPrivateNote = 0;
            foreach (var doc in page)
            {
                if (DocumentScopes.IsPrivateNote(doc.Attributes))
                {
                    skippedPrivateNote++;
                    continue;
                }

                bool exists;
                try
                {
                    exists = await storage.ExistsAsync(doc.MarkdownUri!, ct);
                }
                catch (NotSupportedException)
                {
                    // 縮退クライアント（ストレージ未構成）は在るとも無いとも答えない。構成の不調として止める。
                    return Results.Problem(
                        title: "オブジェクトストレージが構成されていないため、本文の有無を確かめられません。",
                        detail: "ObjectStorage:Endpoint を確かめてください。何もアーカイブしていません。",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // 🔴 NoSuchKey 以外の失敗（バケットが無い・403・5xx・時間切れ）は「無い」ではない。このページは 1 件も保存せず、
                    // 502 で返す（駆動スクリプトは連続失敗として止まる。原因を直して同じ要求を流せば回収できる）。
                    loggers.CreateLogger(typeof(ArchiveBodylessDocumentsEndpoint).FullName!).LogWarning(ex,
                        "Archive body-less: could not probe the body of {DocumentId} at {MarkdownUri}; nothing in this page was archived",
                        doc.Id, doc.MarkdownUri);
                    return Results.Problem(
                        title: "本文の有無を確かめられませんでした。",
                        detail: $"文書 {doc.Id} の存在確認が失敗しました（{ex.GetType().Name}）。このページは何もアーカイブしていません。",
                        statusCode: StatusCodes.Status502BadGateway);
                }

                if (exists) bodyPresent++;
                else missing.Add(doc);
            }

            var logger = loggers.CreateLogger(typeof(ArchiveBodylessDocumentsEndpoint).FullName!);
            var principal = http.User.Identity?.Name ?? "(unnamed)";
            var requestedBy = LogSanitizer.Sanitize(req.RequestedBy, MaxRequestedByLength);
            var reason = LogSanitizer.Sanitize(req.Reason, MaxReasonLength);

            var archived = 0;
            if (!dryRun && missing.Count > 0)
            {
                foreach (var doc in missing) doc.Archive(ChangeNote);
                // FR-06, ADR-0027 §決定, IADR-0608 (#2168): 対象全件の保存と発行を 1 トランザクションで確定する（Transactional Outbox）。
                await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(bus, db, missing, ct);

                foreach (var doc in missing)
                {
                    // 🔴 監査: どの文書を、誰が、何のためにアーカイブしたか（1 件 1 行。基数は文書数で閉じる）。
                    // 確定の後に書く（確定しなかったアーカイブを記録しない）。
                    logger.LogInformation(
                        "Archived body-less document {DocumentId} (createdAt={CreatedAt:O}; body {MarkdownUri} is NoSuchKey) "
                        + "by {Principal} (requestedBy {RequestedBy}; reason {Reason})",
                        doc.Id, doc.CreatedAt, doc.MarkdownUri, principal, requestedBy, reason);
                    archived++;
                }
            }

            logger.LogInformation(
                "Archive body-less ({Mode}; createdBefore {CreatedBefore:O}): page of {Selected} — body missing {Missing}, "
                + "body present {Present}, private notes skipped {PrivateNotes}, archived {Archived}; remaining before this page "
                + "{Remaining} of {Matched} (already archived {AlreadyArchived}; not stored {NotStored}; more={More}) "
                + "by {Principal} (requestedBy {RequestedBy}; reason {Reason})",
                dryRun ? "dry-run" : "apply", before, page.Count, missing.Count, bodyPresent, skippedPrivateNote, archived,
                remaining, matched, alreadyArchived, notStored, next is not null, principal, requestedBy, reason);

            return Results.Ok(new ArchiveBodylessDocumentsResponse(
                DryRun: dryRun,
                CreatedBefore: before,
                Matched: matched,
                Remaining: remaining,
                Selected: page.Count,
                BodyMissing: missing.Count,
                BodyPresent: bodyPresent,
                SkippedPrivateNote: skippedPrivateNote,
                Archived: archived,
                AlreadyArchived: alreadyArchived,
                NotStored: notStored,
                NextCursor: next));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly)
          .WithName("ArchiveBodylessDocuments")
          .Produces<ArchiveBodylessDocumentsResponse>();
    }

    private const string StorageUriPrefix = StorageUri.Scheme + "://";

    // 母集合（カーソルに依らない）: 期限より前・アーカイブ済みでない・本文の参照が storage://。
    // 🔴 アーカイブ済みを外すのが冪等の根拠である（2 回目の走査は 1 回目にアーカイブした文書を選ばない）。
    internal static IQueryable<Document> Candidates(IQueryable<Document> older)
        => older.Where(d => d.Status != DocumentStatus.Archived
            && d.MarkdownUri != null && d.MarkdownUri.StartsWith(StorageUriPrefix));

    // 要求の検証。**先頭の 1 件を、その鍵で返す**（再発行の口と同じ作法）。
    internal static (string Key, string Message)? Validate(ArchiveBodylessDocumentsRequest req)
    {
        if (req.DryRun is null)
            return ("dryRun", "dryRun を明示してください（true で件数と内訳だけを返し、false でアーカイブします）。");
        if (req.CreatedBefore is null)
            return ("createdBefore", "createdBefore を指定してください（この時刻より前に作られた文書だけが対象です。既定はありません）。");
        if (req.Limit is < 1 or > MaxLimit)
            return ("limit", $"limit は 1〜{MaxLimit} で指定してください。");
        if (req.DryRun == false && string.IsNullOrWhiteSpace(req.Reason))
            return ("reason", "アーカイブする呼び出しでは reason が必須です（監査のログへ残します）。");
        if (req.RequestedBy is { Length: > MaxRequestedByLength })
            return ("requestedBy", $"requestedBy は {MaxRequestedByLength} 文字までです。");
        if (req.Reason is { Length: > MaxReasonLength })
            return ("reason", $"reason は {MaxReasonLength} 文字までです。");
        if (req.Cursor is not null && !DocumentPageCursor.TryDecode(req.Cursor, out _))
            return ("cursor", "カーソルが不正です。前の応答の nextCursor をそのまま渡してください。");
        return null;
    }
}

// FR-06, [[IADR-0549]] (#1931 項目 3): 要求。`dryRun` と `createdBefore` は必須。`limit` は既定 100・1〜200。
// `requestedBy` / `reason` は記録のための札（口の主体は駆動スクリプトの機械の client なので、人の名前と理由はここで運ぶ）。
// アーカイブする呼び出し（`dryRun=false`）は `reason` が必須。
public sealed record ArchiveBodylessDocumentsRequest(
    bool? DryRun,
    DateTimeOffset? CreatedBefore,
    int? Limit = null,
    string? Cursor = null,
    string? RequestedBy = null,
    string? Reason = null);

// FR-06, [[IADR-0549]] (#1931 項目 3): 応答。
// `matched` はカーソルに依らない母集合（期限より前・未アーカイブ・本文の参照が storage://）、`remaining` はこの呼び出しの前に残っていた件数。
// `bodyMissing` / `bodyPresent` / `skippedPrivateNote` / `archived` はこのページについて数える。
// `alreadyArchived`（期限より前でアーカイブ済み）と `notStored`（期限より前で本文の参照が無い・storage:// でない）は母集合の外の件数である。
public sealed record ArchiveBodylessDocumentsResponse(
    bool DryRun,
    DateTimeOffset CreatedBefore,
    int Matched,
    int Remaining,
    int Selected,
    int BodyMissing,
    int BodyPresent,
    int SkippedPrivateNote,
    int Archived,
    int AlreadyArchived,
    int NotStored,
    string? NextCursor);
