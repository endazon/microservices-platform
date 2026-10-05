using DataSourceService.Domain;
using Microsoft.EntityFrameworkCore;

namespace DataSourceService.Infrastructure.Persistence;

// SC-22, FR-01, NFR-18, 計画 ADR-0126 決定 4, [[IADR-0501]] 決定 3 (#458 段 S2 の独立監査の指摘):
// 正規の参照（`vault:datasource/<ID>#<キー>`）を `Config` の**そのキー 1 つにだけ**置く。
//
// ■ 🔴 なぜ `Config` 全体を作り直して保存しないか
//   読んだ `Config` を作り直して丸ごと書くと、読んでから書くまでの間に入った別の書き込み
//   （別のキーへの配置・SC-06 の更新）を**古い辞書で上書きして消す**。しかも応答は `reference` と報告する。
//   実 DB では 1 文の条件つき更新（`jsonb_set` をそのキーにだけ。キーが無い・空白だけのときだけ）で置き、
//   **置いた後の実際の状態を読み直して**報告する。読み直しは追跡しない（手元の古い実体を信じない）。
// ■ 移行（スキーマ変更）は要らない。並行性の印（xmin 等）も足さない —— 印を足すと、同期の記録など
//   `Config` に触れない書き込みまで競合で落ちるようになる（射程外の挙動の変更）。
// ■ InMemory（単体テストのプロバイダ）は SQL を実行できないので、従来の実体の更新で置く（並行性は実 DB の試験が測る）。
public static class CredentialReferencePlacement
{
    /// <summary>
    /// <paramref name="key"/> が値を持たないときだけ正規の参照を置き、配置後の供給の事実
    /// （<see cref="DataSourceCredentialSupply"/>）を返す。データソースが消えていれば null。
    /// </summary>
    public static async Task<string?> PlaceAsync(DataSourceDbContext db, DataSource ds, string key, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var supply = ds.PlaceCredentialReference(key);
            await db.SaveChangesAsync(ct);
            return supply;
        }

        var reference = ConnectorSecretReference.CanonicalFor(ds.Id, key);
        // 🔴 条件は「そのキーが無い、または空白だけ」（`CredentialSupplyOf` の `absent` と同じ）。平文・別の参照は置き換えない。
        // 値は全てパラメータで渡す（キーもパラメータ。SQL へ連結しない）。
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "DataSources"
               SET "Config" = jsonb_set(COALESCE("Config", '{}'::jsonb), ARRAY[{{key}}]::text[], to_jsonb({{reference}}::text), true)
             WHERE "Id" = {{ds.Id}}
               AND "Status" = {{DataSourceStatus.Active}}
               AND COALESCE("Config" ->> {{key}}, '') ~ '^\s*$'
            """, ct);

        var current = await db.DataSources.AsNoTracking().FirstOrDefaultAsync(d => d.Id == ds.Id, ct);
        return current?.CredentialSupplyOf(key);
    }
}
