using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Events;
using Platform.Shared.Infrastructure.Foundation.Ports.Storage;
using Wolverine;

namespace DataSourceService.Features.DataSources.Sync;

// FR-01, UC-04, IADR-0051: データソース同期のオーケストレーション。
// コネクタ（Discover/Fetch）→オブジェクトストレージ格納→RawDocumentFetched 発行（Map）を束ね、
// 連続失敗の追跡と継続失敗アラート（UC-04 例外フロー）を行う。手動同期（/sync）と定期同期（HostedService）が共用する。
public sealed class DataSourceSyncService(
    ConnectorRegistry registry,
    IObjectStorageClient storage,
    IMessageBus bus,
    ILogger<DataSourceSyncService> logger)
{
    // UC-04 例外フロー / SC-06（Q14 / #537）: 連続失敗がこの回数に達した時点でアラート（継続失敗の警告）。
    // **しきい値は再試行上限そのものである**（計画 §SC-06「「継続失敗」のしきい値は再試行上限に達した
    // 時点とする」）。値の単一情報源は契約側の DataSourceSyncHealth.DefaultRetryLimit であり、
    // ここで別の数を持たない —— 従前は 3 を独自に持っており、計画が「実装が決めることになる」として
    // 明示的に排した状態だった。
    public const int AlertThreshold = DataSourceSyncHealth.DefaultRetryLimit;

    public async Task<SyncResult> SyncAsync(DataSource source, CancellationToken ct = default)
    {
        var result = await RunAsync(source, ct);
        // UC-04 例外フロー: 増分 watermark（LastSyncedAt）は**完全成功時のみ**前進させる（手動/定期で共通）。
        // discover 失敗・一部 fetch 失敗時は進めない。進めてしまうと、失敗/未取得ファイル（更新日時 <= 失敗時刻）が
        // 次回同期の増分から漏れて二度と再取得されず恒久欠落する（＝再試行の担保）。成功済みファイルの再発行は
        // 決定的 DocumentId により下流が冪等 upsert するため安全。
        if (result.ShouldAdvanceWatermark)
            source.RecordSync();
        return result;
    }

    // FR-05, UC-04, #752 段 1: `SourceItem` が運んできたアイテム単位のメタを ABAC 属性キーへ写す。
    //
    // 🔴 **運んでこなければ null を返す**（＝上書きが 1 件も無い＝挙動不変）。
    //
    // ［2026-09-05 更新 / #752］**`wiki` / `saas` / `db` が `UpdatedBy` を載せるようになった。**
    // 従前ここには「コネクタ 4 実装はいずれも載せないため、常に null である」と書いていたが、
    // いま null になるのは `filesystem`（構造上の縮退。ADR-0074 決定 3）と、
    // 更新者を構成していない・ソース側が空だったソースである。
    //
    // ［2026-09-03 更新 / #1194 / ADR-0074 決定 1・4］**解決段が入った。**
    // 従前ここには「🔴 ここには解決段が無い。`UpdatedBy` はそのまま `owner` になる」と書いていたが、
    // **もう素通ししない。** 解決順 **① Keycloak ユーザー検索 → ② データソース単位の写像表
    // → 予約値 `system`**（裁定 2026-08-16。planning#371）のうち、**② を
    // `DataSource.ResolveOwner` として実装した**（写像表の器は SC-06 の登録・更新フォームが持つ。
    // ADR-0074 決定 1）。**① は未配備のままでよい** —— 解決順は保ったまま ② だけを埋める。
    //
    // 🔴 **写像に当たらなければ null を返す。生の識別子は 1 件も `owner` へ入らない。**
    // 計画は「**別名前空間の識別子をそのまま `owner` へ入れてはならない**」「誤った写像は
    // **偽の所有者**を作り、**裁量制御が意図しない相手に開く**」「安全側は『解決しない』」と
    // 定める（09_datasource-connectors §システム投入経路 / ADR-0036）。
    //
    // **これが ADR-0074 決定 5 が定めた先後の前半である** —— `db` コネクタへ更新者列を載せてよいのは
    // 「解決器が配備された後」であり、それが本メソッドである。
    // **［2026-09-05 / #752］後半（値の搭載）も着地した** —— `wiki` / `saas` は自前 DTO の項目、
    // `db` は opt-in の列から更新者を運ぶ。`filesystem` は構造上運べないままである。
    private static Dictionary<string, string>? PerItemAttributes(DataSource source, SourceItem item)
    {
        var owner = source.ResolveOwner(item.UpdatedBy);
        return owner is null
            ? null
            : new Dictionary<string, string> { [DataSource.OwnerKey] = owner };
    }

    private async Task<SyncResult> RunAsync(DataSource source, CancellationToken ct)
    {
        var connector = registry.Resolve(source.SourceType);
        if (connector is null)
        {
            // filesystem/wiki/saas/db は実装・DI 登録済み（#195/#217/#218/#219）。未登録の SourceType は
            // 5xx にせず縮退する。新規コネクタはプラグイン（DI 登録）追加のみで対応する（IADR-0051）。
            logger.LogInformation(
                "SourceType '{Type}' のコネクタは未実装のため同期をスキップします（source {Id}）",
                source.SourceType, source.Id);
            return new SyncResult(0, 0, ConnectorAvailable: false, DiscoverSucceeded: false,
                Message: $"connector for '{source.SourceType}' not implemented");
        }

        IReadOnlyList<SourceItem> items;
        try
        {
            // 増分: 前回同期時刻を watermark に差分のみ列挙（初回は null＝フルスキャン）。
            items = await connector.DiscoverAsync(source, source.LastSyncedAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AlertOnFailure(source, "discover", ex);
            // FR-01, UC-04, SC-06, IADR-0295 決定 2: **応答も `SyncErrorRedactor` を通す。**
            // 従前ここは `ex.Message` を素で載せており、同じ例外が `AlertOnFailure` 経由では
            // マスクして保存されるのに**応答だけが素通し**だった。`DatabaseConnector` は
            // `builder["Password"]` で接続文字列を合成して `OpenAsync` するため、
            // **Npgsql の接続失敗例外にパスワードが載る経路が実在する。**
            //
            // **文字列を組み立ててから通す。** そうすれば保存される `LastSyncError` と応答が
            // 同じ規則（マスク ＋ 500 文字上限）で揃う。
            //
            // discover 失敗は「成功して 0 件」と区別する（DiscoverSucceeded=false）→ watermark を進めない。
            return new SyncResult(0, 0, ConnectorAvailable: true, DiscoverSucceeded: false,
                Message: SyncErrorRedactor.Redact("discover failed: " + ex.Message));
        }

        var fetched = 0;
        var failed = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var raw = await connector.FetchAsync(source, item, ct);
                var fetchId = Guid.NewGuid();
                // 原本をオブジェクトストレージへ格納（未構成時は NullObjectStorageClient が決定的 URI を返し縮退）。
                var key = $"{source.Id}/{fetchId}/raw{Path.GetExtension(item.Path)}";
                var storageUri = await storage.PutBytesAsync(key, raw.Bytes, raw.ContentType, ct);

                // FR-01, UC-04, SC-05, SC-09, #637: **取り込み経路はタグを生成しない**
                // （計画確定・2026-08-09。利用者裁定 planning#304。正は計画
                // `06_technical/09_datasource-connectors.md` §取り込み経路はタグを生成しない）。
                //
                // **ソースのメタ（所在・部門・フォルダ・更新者等）の写像先は ABAC 基本属性であり、
                // タグではない。**
                //
                // ［2026-08-21 追記 / #752］**従前ここには「それらは上の `attributes` に載っている」と
                // 書いていたが、更新者は載っていなかった。** 属性は `foreach` の**外**でソース単位に
                // 1 回だけ計算されており、全アイテムが同じ辞書の複製を受け取っていた。つまり
                // **アイテムごとに違う更新者を載せる経路が構造上存在しなかった。**
                // 本コミットで属性の解決を**アイテム単位**へ移した（下の `GetEffectiveAttributes(perItem)`）。
                //
                // 従前ここは**親フォルダ名をタグへ写していた**（`BuildTags`）。**削除した。**
                // フォルダ名をタグにすると**ファイルサーバーのディレクトリ名がそのまま辞書になる**うえ、
                // 使用件数が登録の瞬間に 1 件以上となるため、SC-09 の削除拒否により
                // **管理者は増えた値を一切消せなくなる**（[[IADR-0153]] 決定 5）。
                //
                // **コネクタは構造上タグを運べない**（`SourceItem` が運ぶのは所在・更新日時・サイズと、
                // #752 段 1 で足した更新者だけであり、**タグは含まない**）。
                // 将来コネクタがソース側のタグを運ぶようになったら**計画へ改めて裁定を仰ぐ**。

                // Map: データソースの既定 ABAC 属性（機密区分フェイルセーフ含む・IADR-0019）に
                // **アイテム単位の上書き**を重ねて原本へ付与する（#752 段 1）。優先順位は
                // 明示指定 > アイテム単位 > 予約値（`DataSource.GetEffectiveAttributes` を参照）。
                //
                // ［2026-09-03 更新 / #1194］**アイテム単位の上書きは写像表を引いた結果だけである。**
                // 写像に当たらない（または `UpdatedBy` が無い）ときは `perItem` が null になり、
                // `owner` は予約値 `system` へ倒れる（ADR-0074 決定 3。**減らなくてよい**）。
                var attributes = source.GetEffectiveAttributes(PerItemAttributes(source, item));

                // FR-02, FR-03, ADR-0070 決定 4 / [[IADR-0388]] 決定 4 (#1253):
                // **データソースの表示名も運ぶ。** 本文を持たない文書は題名・タグ・所在・
                // データソース名だけが検索の手掛かりであり、名前は下流（索引テキスト）で
                // 人が打つ語である。正本は `source.Id` のままで、これは射影用の複写である。
                await bus.PublishAsync(new RawDocumentFetched(
                    fetchId, source.Id, source.SourceType,
                    item.Path, storageUri, raw.ContentType,
                    attributes, [],
                    DateTimeOffset.UtcNow,
                    source.Name));
                fetched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                // IADR-0295 決定 4: **例外オブジェクトをそのまま渡さない。** `ex` を第 1 引数に渡すと
                // `ILogger` は `Exception.ToString()`（メッセージ ＋ 内部例外のメッセージ ＋ スタック）を
                // ログレコードへ載せる。共通ログ基盤にスクラビングは無いため（`Foundation/` を
                // `redact|scrub|sanitiz|mask` で走査して 0 件）、載せたものはそのまま外へ出る。
                // 型名は資格情報を運ばず切り分けの主要な手掛かりなので残す。
                logger.LogWarning(
                    "原本の取得/発行に失敗: {Path}（source {Id}）: {ErrorType}: {Error}",
                    item.Path, source.Id, ex.GetType().FullName, SyncErrorRedactor.Redact(ex.Message));
            }
        }

        if (failed == 0)
            source.ClearSyncFailures();
        else
            AlertOnFailure(source, $"{failed}/{items.Count} 件の取得に失敗", null);

        logger.LogInformation(
            "同期完了 source {Id}（{Type}）: fetched={Fetched} failed={Failed}",
            source.Id, source.SourceType, fetched, failed);
        return new SyncResult(fetched, failed, ConnectorAvailable: true, DiscoverSucceeded: true, Message: null);
    }

    // UC-04 例外フロー: 連続失敗を記録し、しきい値到達で継続失敗アラート（構造化ログ Alert=true）を出す。
    // SC-06（Q14 / #537）: 計数と直近エラーは**エンティティへ**記録する（永続化され SC-06 が読む）。
    // 永続化は呼び出し側の SaveChangesAsync が行う（手動 /sync・定期同期ワーカーの双方が呼んでいる）。
    // エラーメッセージは保存の時点でマスクする（IADR-0053 と同じ守りを直近エラーの経路にも通す）。
    private void AlertOnFailure(DataSource source, string phase, Exception? ex)
    {
        var count = source.RecordSyncFailure(
            SyncErrorRedactor.Redact(ex?.Message ?? phase), DateTimeOffset.UtcNow);
        // IADR-0295 決定 4: 上と同じ理由で例外オブジェクトを渡さない。**ここは discover 失敗の
        // 経路であり、パスワードを運ぶ実例が確認されている側である。**
        if (ex is not null)
            logger.LogWarning(
                "同期失敗（{Phase}）source {Id} 連続{Count}回: {ErrorType}: {Error}",
                phase, source.Id, count, ex.GetType().FullName, SyncErrorRedactor.Redact(ex.Message));

        if (count >= AlertThreshold)
            logger.LogError(
                "継続失敗アラート: データソース {Name}（{Id}）が {Count} 回連続で同期に失敗しています（{Phase}） {Alert}",
                source.Name, source.Id, count, phase, true);
    }
}

// 同期結果。ConnectorAvailable=false は未対応 SourceType（縮退）。DiscoverSucceeded=false は
// discover が失敗した（＝「成功して 0 件」と区別する）ことを表す。
public sealed record SyncResult(
    int Fetched, int Failed, bool ConnectorAvailable, bool DiscoverSucceeded, string? Message)
{
    // 増分 watermark（LastSyncedAt）を進めてよいのは、コネクタがあり discover が成功し、
    // 全アイテムの取得に成功したとき。失敗があれば進めず、次回同期で再試行できるようにする（UC-04 再試行）。
    public bool ShouldAdvanceWatermark => ConnectorAvailable && DiscoverSucceeded && Failed == 0;
}
