using Amazon.S3;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents.AstStaleCopies;
using DocumentService.Features.Documents.ListPage;
using DocumentService.Infrastructure.Persistence;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Composable.Adapters.Storage;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Logging;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.Documents.StripUntrustedBoundary;

// FR-06, UC-03, [[IADR-0556]] (#1973。AST#1359 / AST#1360・AST の IADR-0542 の後始末):
// **AST が境界語（`<<<UNTRUSTED_DATA` … `UNTRUSTED_DATA>>>`）で囲んだまま保存した文書の表題・本文から、枠を剥がす管理者だけの一回限りの口**
// （`POST /documents/strip-untrusted-boundary`）。
//
// AST は 2026-10-16 まで、KB へ保存するニュースの表題と本文を境界語で囲んでいた。新しい保存は囲まない（AST#1360）が、
// 旧い文書は囲まれたまま台帳・索引・Wiki に残る（一覧に境界語が出る・表題が約 37 字長い・索引の入力に毎回同じ語が混ざる）。
// AST の文書 API の port は表題を更新できず、基盤に upsert も無いので、AST からは直せない。
//
// 🔴 **対象は `project = ai-stock-trading` の組織文書で、アーカイブ済みでないものだけ**である（個人資料・他の project・アーカイブ済みには触らない）。
//   その中で、表題か本文が枠の形（`UntrustedBoundaryFrame.IsWrapped`）をしている文書だけを書き換える。枠の形でなければ何もしない。
// 🔴 **冪等である。** 剥がした後の値は枠の形をしていない（剥がすのは枠が無くなるまで）ので、2 回流しても 2 回目は何も変えない。
// 🔴 **本文は新しいキーへ書き、台帳の参照を付け替える**（`documents/{id}/body-boundary-stripped.md`）。元のオブジェクトは上書きしない ——
//   版の履歴（前の版の本文）が残り、途中で失敗しても台帳と本文が食い違わない（書き込みはすべて台帳の保存の前）。元のオブジェクトは
//   前の版が参照するので、文書の削除で消える（`DocumentObjectPurger` は全版の参照を消す）。
// 🔴 **状態・属性・タグは動かさない**（`Document.ReplaceUntrustedBoundaryWrapped`）。版は 1 つだけ進める。
// 🔴 **下流へは単票の更新と同じ発行の門で伝える**（`PublishUpdatedIfIndexableAsync`）。取り込みは索引を作り直し、Wiki 同期は表題と本文を
//   書き直す（Wiki の路は文書 ID 由来なので、表題が変わっても同じページである）。
// 🔴 **`dryRun` は必須**（省略は 400）。dry-run も同じページ送りで本文を読み、件数と対象の文書 ID だけを返す（何も書かない）。
//   本文が読めない（NoSuchKey 以外の失敗）ときは、そのページを 1 件も書き換えずに 502 で返す。ストレージ未構成は 503。
//
// 量の制御・カナリア・キューと DLQ の監視・中断と再開は駆動スクリプト（`scripts/strip-untrusted-boundary.js`）が持つ。
internal static class StripUntrustedBoundaryEndpoint
{
    public const string Route = "/strip-untrusted-boundary";

    // 版の注記。どの版がこの操作で作られたかを台帳から引くための目印である。**変えない。**
    public const string ChangeNote = "untrusted-boundary stripped from title/body — one-off migration of AST documents (#1973)";

    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 200;
    internal const int MaxRequestedByLength = 100;
    internal const int MaxReasonLength = 500;

    // 剥がした本文の置き場所。元の本文（`DocumentBodyIntake.StorageKey`）とは別のキーにする（上の注記）。
    internal static string StrippedBodyKey(Guid documentId) => $"documents/{documentId:D}/body-boundary-stripped.md";

    internal static void Map(RouteGroupBuilder write)
    {
        write.MapPost(Route, async (StripUntrustedBoundaryRequest req, HttpContext http,
            DocumentDbContext db, IObjectStorageClient storage, IDocumentUpdatedPublisher bus,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (Validate(req) is { } p)
                return Results.ValidationProblem(new Dictionary<string, string[]> { [p.Key] = [p.Message] });

            var logger = loggers.CreateLogger(typeof(StripUntrustedBoundaryEndpoint).FullName!);
            var dryRun = req.DryRun!.Value;
            DocumentPageCursor? after = null;
            if (req.Cursor is not null && DocumentPageCursor.TryDecode(req.Cursor, out var decoded))
                after = decoded;
            var limit = Math.Clamp(req.Limit ?? DefaultLimit, 1, MaxLimit);

            // 母集合: アーカイブ済みでない ∩ `project = ai-stock-trading` ∩ 組織文書（個人資料は `ReadPageAsync` の述語が外す）。
            // 属性の一致は jsonb の値変換のため SQL へ訳せない。台帳を SQL の並びで塊ごとに読み、塊の中で当てる（一覧の口と同じ関数。IADR-0509）。
            var ledger = db.Documents.Where(d => d.Status != DocumentStatus.Archived);
            var (page, next) = await DocumentPageQuery.ReadPageAsync(
                ledger, ProjectFilter, limit, after, readable: null, ct);

            // ページの文書を 1 件ずつ確かめる。🔴 書き込みはすべて確かめ終えてから —— 途中で読めなかったら何も書かない。
            var plans = new List<Plan>();
            var bodyMissing = 0;
            var notStored = 0;
            var titleLeftWrapped = 0;
            var bodyLeftWrapped = 0;
            foreach (var doc in page)
            {
                string? body = null;
                if (!StorageUri.IsStorageUri(doc.MarkdownUri))
                {
                    notStored++;
                }
                else
                {
                    try
                    {
                        body = await storage.GetTextAsync(doc.MarkdownUri!, ct);
                    }
                    catch (AmazonS3Exception ex) when (ObjectStorageErrors.IsNoSuchKey(ex))
                    {
                        // 本文のオブジェクトが無い（切り替え前の文書。#1931 項目 3）。表題だけを見る。
                        bodyMissing++;
                    }
                    catch (NotSupportedException)
                    {
                        return StorageUnavailable();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex,
                            "Strip untrusted boundary: could not read the body of {DocumentId} at {MarkdownUri}; nothing in this page was changed",
                            doc.Id, doc.MarkdownUri);
                        return Results.Problem(
                            title: "本文を読めませんでした。",
                            detail: $"文書 {doc.Id} の本文の読み取りが失敗しました（{ex.GetType().Name}）。このページは何も書き換えていません。",
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                }

                var titleWrapped = UntrustedBoundaryFrame.IsWrapped(doc.Title);
                // 剥がすと空になる表題は剥がさない（表題は必須。空の表題を台帳に作らない）。数えて残す。
                if (titleWrapped && string.IsNullOrWhiteSpace(UntrustedBoundaryFrame.Strip(doc.Title)))
                {
                    titleWrapped = false;
                    titleLeftWrapped++;
                }
                var bodyWrapped = UntrustedBoundaryFrame.IsWrapped(body);
                // 剥がすと空になる本文（枠だけの文書）も剥がさない。空の本文を取り込み・Wiki 同期へ流すと扱いが決まっていないため、
                // 数えて残す（表題と同じ側へ倒す。#1978 のレビュー）。
                if (bodyWrapped && string.IsNullOrWhiteSpace(UntrustedBoundaryFrame.Strip(body)))
                {
                    bodyWrapped = false;
                    bodyLeftWrapped++;
                }
                if (titleWrapped || bodyWrapped)
                    plans.Add(new Plan(doc,
                        titleWrapped ? UntrustedBoundaryFrame.Strip(doc.Title)! : doc.Title,
                        bodyWrapped ? UntrustedBoundaryFrame.Strip(body) : null,
                        titleWrapped, bodyWrapped));
            }

            var principal = http.User.Identity?.Name ?? "(unnamed)";
            var requestedBy = LogSanitizer.Sanitize(req.RequestedBy, MaxRequestedByLength);
            var reason = LogSanitizer.Sanitize(req.Reason, MaxReasonLength);

            var stripped = 0;
            if (!dryRun && plans.Count > 0)
            {
                // 本文を先に新しいキーへ書く（台帳はまだ動かさない）。1 件でも書けなければ台帳を保存せずに返す
                // （書けた分のオブジェクトはどこからも参照されず、次の実行が同じキーへ書き直す）。
                var newUris = new Dictionary<Guid, string>();
                foreach (var plan in plans.Where(x => x.StrippedBody is not null))
                {
                    try
                    {
                        newUris[plan.Doc.Id] = await storage.PutTextAsync(
                            StrippedBodyKey(plan.Doc.Id), plan.StrippedBody!, DocumentBodyIntake.ContentType, ct);
                    }
                    catch (NotSupportedException)
                    {
                        return StorageUnavailable();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex,
                            "Strip untrusted boundary: could not write the stripped body of {DocumentId}; nothing in this page was changed",
                            plan.Doc.Id);
                        return Results.Problem(
                            title: "剥がした本文を書けませんでした。",
                            detail: $"文書 {plan.Doc.Id} の本文の書き込みが失敗しました（{ex.GetType().Name}）。このページは何も書き換えていません。",
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                }

                foreach (var plan in plans)
                {
                    var uri = newUris.GetValueOrDefault(plan.Doc.Id);
                    plan.Doc.ReplaceUntrustedBoundaryWrapped(plan.StrippedTitle, uri,
                        uri is null ? null : DocumentBodyIntake.Fingerprint(plan.StrippedBody!), ChangeNote);
                }
                // FR-06, ADR-0027 §決定, IADR-0608 (#2168): 対象全件の保存と発行を 1 トランザクションで確定する（Transactional Outbox）。
                await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(
                    bus, db, plans.Select(x => x.Doc).ToList(), ct);

                foreach (var plan in plans)
                {
                    // 🔴 監査: どの文書を、誰が、何のために書き換えたか（1 件 1 行）。
                    // 表題・本文に由来する値（中身も、枠の有無の判定も）はこの行に出さない。表題と本文の内訳は応答とページの要約（件数）が持つ。
                    // 確定の後に書く（確定しなかった書き換えを記録しない）。
                    logger.LogInformation(
                        "Stripped untrusted boundary from document {DocumentId} "
                        + "by {Principal} (requestedBy {RequestedBy}; reason {Reason})",
                        plan.Doc.Id, principal, requestedBy, reason);
                    stripped++;
                }
            }

            logger.LogInformation(
                "Strip untrusted boundary ({Mode}): page of {Selected} — wrapped {Wrapped} (title {TitleWrapped}; body {BodyWrapped}), "
                + "body missing {BodyMissing}, not stored {NotStored}, title left wrapped (would be empty) {TitleLeftWrapped}, "
                + "body left wrapped (would be empty) {BodyLeftWrapped}, "
                + "stripped {Stripped}; more={More} "
                + "by {Principal} (requestedBy {RequestedBy}; reason {Reason})",
                dryRun ? "dry-run" : "apply", page.Count, plans.Count, plans.Count(x => x.TitleWrapped), plans.Count(x => x.BodyWrapped),
                bodyMissing, notStored, titleLeftWrapped, bodyLeftWrapped, stripped, next is not null, principal, requestedBy, reason);

            return Results.Ok(new StripUntrustedBoundaryResponse(
                DryRun: dryRun,
                Selected: page.Count,
                Wrapped: plans.Count,
                TitleWrapped: plans.Count(x => x.TitleWrapped),
                BodyWrapped: plans.Count(x => x.BodyWrapped),
                BodyMissing: bodyMissing,
                NotStored: notStored,
                TitleLeftWrapped: titleLeftWrapped,
                BodyLeftWrapped: bodyLeftWrapped,
                Stripped: stripped,
                Items: [.. plans.Select(x => new StripUntrustedBoundaryItem(x.Doc.Id, x.TitleWrapped, x.BodyWrapped))],
                NextCursor: next));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly)
          .WithName("StripUntrustedBoundary")
          .Produces<StripUntrustedBoundaryResponse>();
    }

    private static readonly IReadOnlyDictionary<string, string> ProjectFilter =
        new Dictionary<string, string> { [RestrictedProject.DocumentKey] = AstStaleCopyRules.AstProject };

    private static IResult StorageUnavailable() => Results.Problem(
        title: "オブジェクトストレージが構成されていないため、本文を読み書きできません。",
        detail: "ObjectStorage:Endpoint を確かめてください。何も書き換えていません。",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private sealed record Plan(Document Doc, string StrippedTitle, string? StrippedBody, bool TitleWrapped, bool BodyWrapped);

    // 要求の検証。**先頭の 1 件を、その鍵で返す**（一括アーカイブの口と同じ作法）。
    internal static (string Key, string Message)? Validate(StripUntrustedBoundaryRequest req)
    {
        if (req.DryRun is null)
            return ("dryRun", "dryRun を明示してください（true で件数と対象の ID だけを返し、false で書き換えます）。");
        if (req.Limit is < 1 or > MaxLimit)
            return ("limit", $"limit は 1〜{MaxLimit} で指定してください。");
        if (req.DryRun == false && string.IsNullOrWhiteSpace(req.Reason))
            return ("reason", "書き換える呼び出しでは reason が必須です（監査のログへ残します）。");
        if (req.RequestedBy is { Length: > MaxRequestedByLength })
            return ("requestedBy", $"requestedBy は {MaxRequestedByLength} 文字までです。");
        if (req.Reason is { Length: > MaxReasonLength })
            return ("reason", $"reason は {MaxReasonLength} 文字までです。");
        if (req.Cursor is not null && !DocumentPageCursor.TryDecode(req.Cursor, out _))
            return ("cursor", "カーソルが不正です。前の応答の nextCursor をそのまま渡してください。");
        return null;
    }
}

// FR-06, [[IADR-0556]] (#1973): 要求。`dryRun` は必須。`limit` は既定 100・1〜200。書き換える呼び出し（`dryRun=false`）は `reason` が必須。
public sealed record StripUntrustedBoundaryRequest(
    bool? DryRun,
    int? Limit = null,
    string? Cursor = null,
    string? RequestedBy = null,
    string? Reason = null);

// FR-06, [[IADR-0556]] (#1973): 応答。件数はすべてこのページについて数える。
// `wrapped` は表題か本文が枠の形をしていた文書の数（`items` の件数と同じ）。`stripped` は書き換えた数（dry-run は 0）。
// `bodyMissing` は本文のオブジェクトが無かった（NoSuchKey）数、`notStored` は本文の参照が無い・storage:// でない数（どちらも表題だけを見る）。
// `titleLeftWrapped` は剥がすと空になるので表題を剥がさなかった数（表題は必須）。`bodyLeftWrapped` は同じく本文を剥がさなかった数
// （空の本文を下流へ流さない）。
public sealed record StripUntrustedBoundaryResponse(
    bool DryRun,
    int Selected,
    int Wrapped,
    int TitleWrapped,
    int BodyWrapped,
    int BodyMissing,
    int NotStored,
    int TitleLeftWrapped,
    int BodyLeftWrapped,
    int Stripped,
    IReadOnlyList<StripUntrustedBoundaryItem> Items,
    string? NextCursor);

// 対象の文書（表題・本文の中身は返さない）。
public sealed record StripUntrustedBoundaryItem(Guid Id, bool Title, bool Body);
