using DataSourceService.Api.Foundation.Domain;
using DataSourceService.Api.Foundation.Persistence;
using DataSourceService.Api.Foundation.Services;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;

namespace DataSourceService.Api.Foundation.Endpoints;

// FR-01, UC-04: データソース管理エンドポイント
public static class DataSourceEndpoints
{
    public static IEndpointRouteBuilder MapDataSourceEndpoints(this IEndpointRouteBuilder app)
    {
        // FR-09, IADR-0044: 多層防御。データソースは運用資産で、閲覧・操作は管理者・運用者に限定する
        // （[[IADR-0039]] の BFF ゲートと同一要件）。BFF 迂回の直接呼び出しでも認可を実効化する
        // （サービスが最終防衛線）。利用者トークンは BFF が後段へ伝播する。
        var g = app.MapGroup("/datasources").WithTags("DataSources")
            .RequireAuthorization(p => p.RequireRole(
                PlatformAuthPolicies.AdminRole,
                PlatformAuthPolicies.OperatorRole));

        // IADR-0053, claude-review #222: 応答では Config 内の秘密（apiToken 等）をマスクする。
        // Vault 移行までの暫定措置。admin/operator であっても API 応答で平文の資格情報を露出させない。
        //
        // SC-06（planning#200 / 裁定 Q15）, IADR-0136: 次回同期は**全ソース同値**である。一覧では
        // SyncSchedule を**1 回だけ**読んでその値を全行へ配る（行ごとに読むと境界を跨いだ瞬間に
        // 列内で値が割れ、「ソースごとに時刻が違う」という持たないはずの意味を生む）。
        g.MapGet("/", async (DataSourceDbContext db, SyncSchedule schedule) =>
        {
            var nextSyncAt = schedule.NextRunAt;
            return Results.Ok((await db.DataSources.ToListAsync()).Select(ds => ToResponse(ds, nextSyncAt)));
        });

        g.MapGet("/{id:guid}", async (Guid id, DataSourceDbContext db, SyncSchedule schedule) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            return ds is null ? Results.NotFound() : Results.Ok(ToResponse(ds, schedule.NextRunAt));
        });

        // FR-01, SC-06（#628）: 登録は**管理者限定**である（計画 §SC-06「登録・更新・無効化は管理者限定」・
        // 裁定 Q19「破壊的操作は管理者限定を維持する」）。グループ既定（admin ＋ operator）は
        // **閲覧の下限**を表すので残し、本エンドポイントだけ AdminOnly を積む（AND 合成で実効 admin のみ。
        // [[IADR-0128]] 決定 1 が #501 で確立した形）。
        g.MapPost("/", async (CreateDataSourceRequest req, DataSourceDbContext db, SyncSchedule schedule) =>
        {
            // FR-01, FR-05: 既定 ABAC 属性（機密区分）を伴ってデータソースを登録する。
            var ds = DataSource.Create(req.Name, req.SourceType, req.ConnectionUri,
                req.Config, req.DefaultAttributes);
            db.DataSources.Add(ds);
            await db.SaveChangesAsync();
            return Results.Created($"/datasources/{ds.Id}", ToResponse(ds, schedule.NextRunAt));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-01, UC-04: 手動同期トリガー（IADR-0051）。実コネクタ経由で原本を取得・格納し
        // RawDocumentFetched を発行する。既定 ABAC 属性（機密区分・IADR-0019）は同期サービスが Map で付与する。
        // filesystem/wiki/saas/db コネクタは実装・DI 登録済み（#195/#217/#218/#219）。未登録の SourceType は
        // 縮退（連携件数 0・5xx にしない）。新規コネクタはプラグイン（DI 登録）追加のみで対応する（IADR-0051）。
        //
        // **認可はグループ既定（admin ＋ operator）のままである**（#628 / planning#299 で裁定・2026-08-09）。
        // 計画は手動同期を**破壊的操作に含めない** —— 外部システムへ接続して取り込みを走らせるが、
        // 増分同期と変換の冪等性により既存データを壊さないためであり、**運用者が SC-10 で異常に気づいた
        // その場で再同期して一次対応できること**を優先する。**登録・無効化と同じに扱わないこと。**
        // ［範囲］人手補正（Phase 2）の導入時に本分類を再確認する（UC-06 の「再変換は補正を破棄する前に警告」が
        // 手動同期由来の再変換にも及ぶかは未確定。補正投稿 API が未実装なので現時点では実害が無い）。
        g.MapPost("/{id:guid}/sync", async (Guid id, DataSourceDbContext db,
            DataSourceSyncService sync) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();

            // 増分 watermark（LastSyncedAt）の前進は SyncAsync が完全成功時のみ実施する
            // （失敗時は進めず次回再試行。UC-04 例外フロー）。ここでは永続化のみ行う。
            var result = await sync.SyncAsync(ds);
            await db.SaveChangesAsync();

            return Results.Accepted($"/datasources/{id}/sync", new
            {
                fetched = result.Fetched,
                failed = result.Failed,
                connectorAvailable = result.ConnectorAvailable,
                message = result.Message,
            });
        });

        // FR-01, UC-04, SC-06（Q16 / #534）: 更新（全置換）。従前は更新の口が無く、登録済みソースの変更が
        // 「削除→再登録」でしかできなかった。削除→再登録は **ID と履歴を切る**（認証情報のローテーションの
        // たびに文書の出所の追跡が切れるのは監査上受け入れがたい）。
        //
        // **認可は管理者限定である**（計画 §SC-06「登録・更新・無効化は管理者限定」）。グループ既定は
        // admin + operator なので、本エンドポイントだけ AdminOnly を上書きで要求する。
        // **無効（disabled）なソースも更新できる** —— 無効化は論理削除であり、認証情報のローテーションは
        // 無効中にも起こる。
        g.MapPut("/{id:guid}", async (Guid id, UpdateDataSourceRequest req, DataSourceDbContext db,
            SyncSchedule schedule) =>
        {
            // AI レビュー 🟡（#627）: **省略を受理しない。** PUT は全置換なので「省略 ＝ 空で置換」は
            // 意味論としては筋が通るが、契約が省略を許していると**うっかりで秘密が消える**
            // （`config` を送り忘れた PUT が apiToken を丸ごと落とす）。消したいなら `{}` と明示させる。
            // **null を現状維持にする道は採らない** —— それをやると PUT と PATCH の区別が消える。
            if (req.Config is null || req.DefaultAttributes is null)
                return Results.BadRequest(new
                {
                    error = "PUT は全置換です。config と defaultAttributes を明示してください"
                          + "（消す場合は {} を送る）。一部だけ変更するなら PATCH を使ってください。",
                });

            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();

            ds.Update(req.Name, req.SourceType, req.ConnectionUri, req.Config, req.DefaultAttributes);
            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(ds, schedule.NextRunAt));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-01, UC-04, SC-06（Q16 / #534）: 部分更新。**null の項目は現状維持**である。
        // 接続先だけ・認証情報だけを差し替える日常運用を、他項目を読んで書き戻す往復なしに行えるようにする
        // ——往復させると応答のマスク済みの値（*** ・IADR-0053）を書き戻して秘密を破壊する。
        g.MapPatch("/{id:guid}", async (Guid id, PatchDataSourceRequest req, DataSourceDbContext db,
            SyncSchedule schedule) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();

            ds.Patch(req.Name, req.SourceType, req.ConnectionUri, req.Config, req.DefaultAttributes);
            await db.SaveChangesAsync();
            return Results.Ok(ToResponse(ds, schedule.NextRunAt));
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        // FR-01, SC-06（#628）: 無効化（論理削除）は**管理者限定**である。POST と同じく AdminOnly を積む。
        g.MapDelete("/{id:guid}", async (Guid id, DataSourceDbContext db) =>
        {
            var ds = await db.DataSources.FindAsync(id);
            if (ds is null) return Results.NotFound();
            ds.Disable();
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(PlatformAuthPolicies.AdminOnly);

        return app;
    }

    // IADR-0053, claude-review #222: API 応答用の投影。エンティティをそのまま返すと Config 内の
    // 秘密（apiToken 等）が平文露出するため、秘密キーの値をマスクして返す（Vault 移行までの暫定）。
    // SC-06: nextSyncAt はエンティティに持たない導出値（共通間隔の次回実行時刻）であり、呼び出し側が渡す。
    private static object ToResponse(DataSource ds, DateTimeOffset? nextSyncAt) => new
    {
        ds.Id,
        ds.Name,
        ds.SourceType,
        ds.ConnectionUri,
        ds.Status,
        ds.LastSyncedAt,
        Config = RedactSecrets(ds.Config),
        ds.DefaultAttributes,
        ds.CreatedAt,
        NextSyncAt = nextSyncAt,
        // SC-06（Q14 / #537）: 同期健全性。RetryLimit は画面が「3/5」の分母を契約から得るために返す
        // （画面へ定数を複写すると IADR-0127 決定 2 が禁じた「契約から導出できない表示」に戻る）。
        // LastSyncError は保存時点でマスク済み（SyncErrorRedactor）。
        ds.ConsecutiveFailureCount,
        RetryLimit = DataSourceSyncService.AlertThreshold,
        ds.LastSyncError,
        ds.LastSyncErrorAt,
    };

    // 秘密キーの定義とマスク値は SecretConfigMask が単一情報源である（IADR-0148 決定 6）。
    // **読み（ここ）と書き（DataSource.Update / Patch）で同じ知識を使う** —— 2 箇所に持つと、
    // マーカーを足したときに片方が黙って古くなる。
    private static Dictionary<string, string> RedactSecrets(IReadOnlyDictionary<string, string> config)
        => SecretConfigMask.Redact(config);
}

public record CreateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config,
    // FR-05: 原本へ付与する既定 ABAC 文書属性（confidentiality 等）。未指定時は internal を補完。
    Dictionary<string, string>? DefaultAttributes = null);

// FR-01, UC-04, SC-06（Q16 / #534）: 更新（全置換）要求。契約側の Knowledge.Contracts.Dtos の
// 同名レコードと JSON 互換である（本サービスは SPA 契約に依存せず自前の入力型を持つ既存の作法に倣う）。
// **Id / CreatedAt / LastSyncedAt / 同期健全性は含まない** —— 更新で履歴を巻き戻せてはならない。
// **Config / DefaultAttributes の省略は 400 で拒否する**（AI レビュー 🟡 / #627）。型としては
// nullable だが、null は「未指定」であって「空にする」ではない —— 消すなら {} を明示させる。
public record UpdateDataSourceRequest(
    string Name,
    string SourceType,
    string ConnectionUri,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null);

// FR-01, UC-04, SC-06（Q16 / #534）: 部分更新要求。**null は「変更しない」を意味する**。
public record PatchDataSourceRequest(
    string? Name = null,
    string? SourceType = null,
    string? ConnectionUri = null,
    Dictionary<string, string>? Config = null,
    Dictionary<string, string>? DefaultAttributes = null);
