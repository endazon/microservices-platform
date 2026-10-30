using System.Text;
using DocumentService.Domain;
using DocumentService.Domain.Ports;
using DocumentService.Features.Documents;
using DocumentService.Features.PrivateNotes;
using DocumentService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Audit;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;

namespace DocumentService.Features.SyncConflicts.Resolve;

// FR-20, UC-11, SC-20 主要素 5, ADR-0037 決定 7・17, [[IADR-0352]], #1442: 競合の解決（3 択）。
//
// - `local`: 端末の本文を**新しい版**として資料へ書く（サーバ版を土台に 1 版進む）。
// - `server`: サーバ版をそのまま採る。**資料は 1 バイトも変わらない**（版も進めない）。
// - `both`: サーバ版はそのまま、端末の本文を**別名の新規資料**として作る。
//   🔴 **新規作成なので容量上限が効く**（100% 到達時は 507。ADR-0037 決定 17）——
//   「両方を残す」を容量の例外にすると、上限が事実上無くなる。
//
// **自動解決は無い。** どの分岐も利用者が選んだ結果の適用であり、サーバが選ぶ経路は存在しない。
//
// 🔴 **ローカル本文の削除は SaveChanges の前**である（IADR-0296 決定 3 と同じ向き）——
// 先に解決済みにしてから削除に失敗すると、**解決済みなのに本文だけが残り、誰も消せない**。
// 前に置けば、失敗したときは何も確定せず利用者が選び直せる。
//
// FR-19, ADR-0061 決定 1・2, [[IADR-0396]] 決定 4, [[IADR-0455]] 決定 1, #1474:
// 🔴 **本文を書き換える／本文を持つ資料を作る分岐は、発行の門を通して `DocumentUpdated` を出す。**
// 出さないと、露出 ON の個人資料は本文だけ新しくなり、索引（検索・グラフ）は古い本文のまま残る。
// どちらの分岐も属性を書き換えない（`local` は現在の属性をそのまま渡す／`both` は新規作成）ので、
// 単純な門（`PublishUpdatedIfIndexableAsync`）を使う。発行は保存の後で、保存と同じ確定に載る（push と同じ。IADR-0608）。
internal static class ResolveSyncConflictEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/resolve", async (Guid id, ResolveSyncConflictRequest req,
            IValidator<ResolveSyncConflictRequest> validator, HttpContext http,
            DocumentDbContext db, IObjectStorageClient storage, IPrivateNoteNotifier notifier,
            IAuditLogger audit, IDocumentUpdatedPublisher bus, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (PrivateNoteEndpoints.SubjectOf(http) is not { } owner) return Results.Unauthorized();

            // 🔴 **401 の後ろ・DB 照会の前**（他の端点と同じ順。前へ出すと 404 が 400 に化ける）。
            var gate = validator.Validate(req);
            if (!gate.IsValid) return ValidationProblems.FirstViolation(gate);

            var conflict = await SyncConflictEndpoints.FindOwnedAsync(db, owner, id, ct);
            if (conflict is null) return Results.NotFound();
            // 解決済みへの再解決は 409 —— 自分の競合であることは判っており、黙って 2 度目を
            // 適用すると版がもう 1 つ進む（`local` で顕著）。
            if (conflict.IsResolved)
                return Results.Conflict(new
                {
                    error = "already_resolved",
                    resolution = conflict.Resolution,
                    resolvedAt = conflict.ResolvedAt,
                });

            var note = await db.PrivateNotes.FindAsync([conflict.DocumentId], ct);
            var doc = await db.Documents.FindAsync([conflict.DocumentId], ct);
            if (note is null || doc is null) return Results.NotFound();
            // #1474（PR #1476 のフェーズ末監査 D1）: **ゴミ箱の資料へは本文を書かない。**
            // push（`ObsidianSync/Push`）・移動と同じく 409 `deleted` を返す。`local` / `both` を通すと、
            // 利用者がゴミ箱へ移した資料の本文が新しくなり、露出 ON なら索引へ再発行されてしまう。
            // `server` は資料を変えないので、競合を片付ける手段として残す。
            if (note.IsDeleted && req.Resolution != SyncConflictResolutions.Server)
                return Results.Conflict(new { error = "deleted", purgeAt = note.PurgeAt });

            // 🔴 FR-20, SC-20 (#2307): ローカル版の本文の**参照が無い**まま `local` / `both` を確定しない。`ReadAsync` は参照が無いと
            // 空文字を「取り出せた」として返すので、従前は**空の本文が新しい版として書かれ**、`both` は空の別名資料を作った。
            // 再試行しても直らない恒久の状態なので 409（503 の `local_content_unavailable` は一時的な取り出しの失敗）。何も確定せず、
            // 競合は未解決のまま残る（`server` は本文を使わないので選べる）。
            if (req.Resolution != SyncConflictResolutions.Server && string.IsNullOrEmpty(conflict.LocalContentUri))
                return Results.Conflict(new { error = "local_content_missing" });

            var now = DateTimeOffset.UtcNow;
            // `server` は本文を使わないので読みに行かない（#2305 の AI レビュー。障害の経路を減らす）。
            var (localContent, localUnavailable) = req.Resolution == SyncConflictResolutions.Server
                ? (string.Empty, false)
                : await Get.GetSyncConflictEndpoint.ReadAsync(storage, conflict.LocalContentUri,
                    loggers.CreateLogger(typeof(ResolveSyncConflictEndpoint).FullName!), "local", ct);
            // 🔴 NFR-19, #2141: ローカル版の本文を取り出せないまま `local` / `both` を確定しない。従前は取り出しの失敗を
            // 空文字に畳んでいたので、**空の本文が新しい版として書かれた**。何も確定せず 503 を返す（競合は未解決のまま残り、選び直せる）。
            if (localUnavailable && req.Resolution != SyncConflictResolutions.Server)
                return Results.Json(new { error = "local_content_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            // 索引の生産側へ流す候補（`server` は資料を変えないので null のまま）。
            Document? written = null;

            if (req.Resolution == SyncConflictResolutions.Local)
            {
                await storage.PutTextAsync(DocumentBodyIntake.StorageKey(doc.Id), localContent,
                    DocumentBodyIntake.ContentType, ct);
                doc.RecordContentFingerprint(DocumentBodyIntake.Fingerprint(localContent));
                // 属性は現在の値をそのまま渡す（書き換えない）—— 撤収の形の門は要らない。
                doc.Update(doc.Title, doc.Attributes, doc.Tags.ToList(), "conflict-resolve-local");
                note.RecordBody(Encoding.UTF8.GetByteCount(localContent),
                    DocumentBodyIntake.Fingerprint(localContent), now);
                written = doc;
            }
            else if (req.Resolution == SyncConflictResolutions.Both)
            {
                var created = await CreateAliasNoteAsync(db, storage, owner, note, doc,
                    localContent, now, ct);
                if (created.Problem is not null) return created.Problem;
                written = created.Doc;
            }
            // `server`: 何もしない（資料はサーバ版のまま。版も進まない）。

            conflict.Resolve(req.Resolution, now);
            // #1474: 確定した後に門を通す（`local` は元の資料・`both` は別名資料）。
            // FR-20, ADR-0027 §決定, IADR-0608 (#2168): 保存と発行を 1 トランザクションで確定する（Transactional Outbox）。
            // 確定が失敗すれば例外がそのまま出て、下の使用量・監査へ進まない（解決していない競合を記録しない）。
            // FR-02, [[IADR-0597]] / IADR-0637 決定 7 (#2150 段 1c): 負け得るのは元の資料を書く `local` だけである（`both` は別名資料の新規作成、
            // `server` は競合の記録だけ）。負けたら競合の解決ごと巻き戻り、409 `version_conflict` を返す（競合は未解決のまま残り、選び直せる）。
            // `local` の本文のオブジェクトは上で上書き済みで戻らない（PutBody と同じ既存の性質）。
            try
            {
                if (written is not null)
                    await DocumentEndpoints.SaveChangesAndPublishUpdatedIfIndexableAsync(bus, db, written, ct);
                else
                    await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (DocumentVersionConflict.Is(ex))
            {
                return await DocumentVersionConflict.AfterLostSaveAsync(db, doc.Id, expectedVersion: null, ct);
            }

            // FR-20, IADR-0637 決定 7 (#2150 段 1c): 退避したローカル本文は**確定の後に**消す。従前は確定の前に消しており、確定が負けると
            // 衝突は未解決のまま残るのに本文だけが消え、`local` を選び直すと空の本文が書かれた（409 をやり直せる失敗にするための順序）。
            // 確定の後に消せなかった分は孤児として残るだけで、解決の結果は変わらない（失敗は応答にしない）。
            if (!string.IsNullOrEmpty(conflict.LocalContentUri))
            {
                try
                {
                    await storage.DeleteAsync(conflict.LocalContentUri, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    loggers.CreateLogger(typeof(ResolveSyncConflictEndpoint).FullName!).LogWarning(ex,
                        "解決した衝突 {ConflictId} の退避したローカル本文を消せなかった（解決は確定済み。オブジェクトは残る）", conflict.Id);
                }
            }

            // FR-22 ②: `both` で使用量が増えるため、警告の跨ぎを再評価する。
            await PrivateNoteUsage.RecordUsageAndWarnAsync(db, notifier, owner, now, ct);

            // ADR-0037 決定 9: 監査は「誰が・いつ・何件」。**タイトル・本文は記録しない。**
            // 🔴 （PR #1476 のフェーズ末監査 D2）保存が確定していれば、解決の監査行は必ず残す。
            // ［IADR-0608］発行は保存と同じ確定に載ったので、確定の後の送出の失敗は例外にならない（Outbox から再送される）。
            audit.Record("private-note.sync.conflict-resolve", owner, "granted",
                $"conflict={conflict.Id} resolution={req.Resolution} count=1");

            var createdNoteId = req.Resolution == SyncConflictResolutions.Both ? written?.Id : null;
            return Results.Ok(new ResolveSyncConflictResponse(conflict.Id, conflict.DocumentId,
                req.Resolution, doc.Version, createdNoteId));
        });
    }

    // SC-20 主要素 5: `both` の別名資料。**新規作成の規則（容量上限・経路衝突）を通す** ——
    // 既存の作成経路（`PrivateNoteEndpoints` の 507 / 409）と同じ応答であり、別の規則を作らない。
    //
    // FR-19, FR-20, SC-20 主要素 5, ADR-0105 決定 1〜4, #1498: 別名資料が元の資料から引き継ぐのは**タグだけ**である。
    //
    // | 項目 | 扱い | 機序 |
    // | --- | --- | --- |
    // | タグ | 🔴 **引き継ぐ**（決定 3） | 元の資料（解決時点のサーバ版）の `Tags` を写す。タグは組織共通の辞書への参照であり秘密性を持たない |
    // | 露出 3 トグル | 引き継がない（決定 1。**資料単位の明示の OFF** で書く＝決定 4） | `PrivateNoteDefaults` |
    // | 共有先 | 引き継がない（決定 2） | 共有台帳は `DocumentId` で引く。別名資料は新しい ID なので 1 件も写らない（**ここで台帳へ行を足さない**） |
    // | 版履歴 | 引き継がない（決定 3） | 新しい `Document` は 1 版から始まる |
    // | 機密区分 | 引き継がない（`restricted`。SC-20 主要素 5 の表） | `PrivateNoteDefaults` |
    //
    // 🔴 **写すのは識別子の集合そのもの**（辞書との突き合わせをここで行わない）。元の資料に付いている時点で
    // 辞書の値域は通過しており（`TagResolver.ToIdsAsync`）、参照のあるタグは削除が拒否される（SC-09）。
    // 受け入れる副作用: 写した各タグの使用件数が 1 増え、写しがある間はそのタグを削除できない（決定 3）。
    private static async Task<(Document? Doc, IResult? Problem)> CreateAliasNoteAsync(
        DocumentDbContext db, IObjectStorageClient storage, string owner, PrivateNote source,
        Document sourceDoc, string localContent, DateTimeOffset now, CancellationToken ct)
    {
        var bytes = (long)Encoding.UTF8.GetByteCount(localContent);

        // ADR-0037 決定 17: 100% 到達時（および上限を跨ぐ場合）は**新規作成を拒否**する。
        var used = await PrivateNoteUsage.UsedBytesAsync(db, owner, ct);
        var quota = await PrivateNoteUsage.GetOrCreateQuotaAsync(db, owner, now, ct);
        if (quota.RejectsNewNote(used, bytes))
            return (null, PrivateNoteEndpoints.QuotaExceededProblem(used, quota.LimitBytes));

        var vaultPath = ConflictAlias.PathOf(source.VaultPath, now);
        if (await PrivateNoteEndpoints.ActivePathExistsAsync(db, owner, vaultPath, ct))
            return (null, PrivateNoteEndpoints.PathConflictProblem(vaultPath));

        var id = Guid.NewGuid();
        var uri = await storage.PutTextAsync(DocumentBodyIntake.StorageKey(id), localContent,
            DocumentBodyIntake.ContentType, ct);
        // 既定（doc_scope=private-note / owner / restricted / 露出 3 トグル OFF）は作成経路と同じ。
        // ［2026-10-30 / #2120］ただし作成経路の AI 入力は利用者単位の既定で始まるのに対し、別名の資料は常に OFF（ADR-0143 決定 3）。
        // 🔴 **元の資料の露出を継がない**（新規作成の既定。FR-21 受け入れ基準 ⑩・ADR-0105 決定 1）。
        // #1474: 呼び出し側で門を通す（push の新規作成と同じ形）。露出は OFF で作られるため、
        // 現行の既定では門に弾かれ、索引の生産側へは何も流れない（ADR-0061 決定 2）。
        // ADR-0105 決定 3 (#1498): タグだけは元の資料から写す（別のリストにする —— 元の資料と同じ実体を共有しない）。
        var doc = Document.CreateWithBody(id, ConflictAlias.TitleOf(sourceDoc.Title, now), uri,
            originalUri: null, contentType: DocumentBodyIntake.ContentType,
            // 🔴 FR-19, 計画 ADR-0143 決定 3 (#2120): 別名コピーには AI 入力の利用者単位の既定を**当てない**（明示の false）。
            attributes: PrivateNoteEndpoints.PrivateNoteDefaults(owner, includeInAi: false), tags: [.. sourceDoc.Tags],
            contentFingerprint: DocumentBodyIntake.Fingerprint(localContent));
        db.Documents.Add(doc);
        db.PrivateNotes.Add(PrivateNote.Create(id, owner, vaultPath, bytes,
            DocumentBodyIntake.Fingerprint(localContent), now));
        return (doc, null);
    }
}
