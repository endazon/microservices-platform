using System.Data.Common;
using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataSourceService.Infrastructure.Secrets.Migration;

// NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, [[IADR-0664]] 決定 3 (#458 段 S4): **設定の平文の資格情報を Vault へ移し、正規の参照へ置き換える**一回きりの移送。
//
// 行（状態に依らず全行。無効化した行の平文も保管の対象である）× 平文のキー（`PlaintextCredential`）ごとに、次の順で行う。
//   1. Vault の `datasource/<ID>` を読む（現在版と既存のキー）。
//   2. 既存のキーに平文を足して、読んだ版で check-and-set で書く（同じパスの別のキーを消さない・並行する書き込みに勝たない）。
//      Vault に既に**同じ値**があれば書かない（再実行・途中で落ちた後の再開）。**違う値**があれば書かず、行を「衝突」として残す
//      （SC-22 から書いた値を平文で上書きしない —— どちらが正しいかは人が決める）。
//   3. 読み直して、書いた値と同じであることを確かめる（値は比べるだけで出さない）。
//   4. 設定のそのキーを、**読んだ値と今の値が同じときだけ**正規の参照へ置き換える（1 文の条件つき更新。`CredentialMigrationStore`）。
// 🔴 **順序は Vault が先、設定が後**（逆だと、値の無い Vault を指す参照が残り、同期が `not-found` で止まる）。
//    1〜3 が失敗した行は設定を触らない（平文のまま。旗が真の間は同期も止まらない）。
// 🔴 **冪等**: 2 回目は平文が無いので何もしない。途中で落ちても、続きから流し直せばよい。
// 🔴 **ログは件数と行の番号だけ**。ID・キー名・値・パスを出さない（行の番号はこの実行の中の通し番号で、`--dry-run` と同じ順序）。
public sealed class ConnectorCredentialMigrator(
    DataSourceDbContext db, MigrationVaultKvClient vault, ILogger<ConnectorCredentialMigrator> logger)
{
    public sealed record Report(int Rows, int PlaintextKeys, int Migrated, int Conflicts, int Failed, bool DryRun)
    {
        public bool Succeeded => Conflicts == 0 && Failed == 0;
    }

    public async Task<Report> RunAsync(bool dryRun, CancellationToken ct)
    {
        var sources = await db.DataSources.AsNoTracking().OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToListAsync(ct);
        int rows = 0, keys = 0, migrated = 0, conflicts = 0, failed = 0;
        var number = 0;
        foreach (var source in sources)
        {
            var plaintextKeys = PlaintextCredential.KeysOf(source.Config);
            if (plaintextKeys.Count == 0)
                continue;
            rows++;
            number++;
            foreach (var key in plaintextKeys)
            {
                keys++;
                if (dryRun)
                    continue;
                var outcome = await MigrateOneAsync(source.Id, key, source.Config[key], ct);
                switch (outcome)
                {
                    case Outcome.Migrated:
                        migrated++;
                        break;
                    case Outcome.Conflict:
                        conflicts++;
                        logger.LogWarning("移送: 行 #{Row} の 1 キーは Vault に別の値がある（設定は平文のまま。SC-22 から書いた値と食い違う）", number);
                        break;
                    default:
                        failed++;
                        logger.LogWarning("移送: 行 #{Row} の 1 キーを移せなかった（設定は平文のまま）: {Reason}", number, outcome);
                        break;
                }
            }
        }

        var report = new Report(rows, keys, migrated, conflicts, failed, dryRun);
        logger.LogInformation(
            "移送{Mode}: 平文を持つ行 {Rows}・平文のキー {Keys}・移した {Migrated}・衝突 {Conflicts}・失敗 {Failed}",
            dryRun ? "（dry-run。何も書いていない）" : string.Empty, rows, keys, migrated, conflicts, failed);
        return report;
    }

    internal enum Outcome { Migrated, Conflict, VaultUnavailable, VerifyMismatch, Raced, StoreFailed }

    // NFR-18, IADR-0664 決定 3 (#2376 の指摘 4): 移送の途中の DB の例外。未処理例外で落とさず、失敗として数える（終了コード 1）。
    // 🔴 例外文は出さない（接続先・SQL の断片を運び得る）。型名だけ。
    internal static bool IsStoreFailure(Exception ex) =>
        ex is DbException or DbUpdateException or InvalidOperationException or TimeoutException;

    private async Task<Outcome> MigrateOneAsync(Guid id, string key, string plaintext, CancellationToken ct)
    {
        var path = ConnectorSecretReference.PathFor(id);
        try
        {
            var current = await vault.ReadAsync(path, ct);
            if (current.Data.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing, plaintext, StringComparison.Ordinal))
                    return Outcome.Conflict;
            }
            else
            {
                var merged = new Dictionary<string, string>(current.Data, StringComparer.Ordinal) { [key] = plaintext };
                await vault.WriteAsync(path, merged, current.Version, ct);
            }

            var reread = await vault.ReadAsync(path, ct);
            if (!reread.Data.TryGetValue(key, out var stored) || !string.Equals(stored, plaintext, StringComparison.Ordinal))
                return Outcome.VerifyMismatch;
        }
        catch (Exception ex) when ((ex is VaultMigrationException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            // 🔴 例外文・例外オブジェクトを出さない（URL＝パスを運び得る）。型名と状態コードだけ。
            logger.LogWarning("移送: Vault の呼び出しが失敗した: {ExceptionType} {Status}",
                ex.GetType().Name, (ex as VaultMigrationException)?.Status);
            return Outcome.VaultUnavailable;
        }

        try
        {
            return await CredentialMigrationStore.ReplaceAsync(db, id, key, plaintext, ct)
                ? Outcome.Migrated
                : Outcome.Raced;
        }
        catch (Exception ex) when (IsStoreFailure(ex) && !ct.IsCancellationRequested)
        {
            // 保管先には書けている。設定は平文のまま（流し直せば「同じ値」の経路で置き換えへ進む）。
            logger.LogWarning("移送: 設定の置き換え（DB）が失敗した: {ExceptionType}", ex.GetType().Name);
            return Outcome.StoreFailed;
        }
    }
}

// NFR-18, [[IADR-0664]] 決定 3: 設定の 1 キーを、**読んだ平文と今の値が同じときだけ**正規の参照へ置き換える。
// 実 DB は 1 文の条件つき更新（`CredentialReferencePlacement` と同じ作法。`Config` 全体を作り直して書かない）。
public static class CredentialMigrationStore
{
    public static async Task<bool> ReplaceAsync(DataSourceDbContext db, Guid id, string key, string plaintext, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var ds = await db.DataSources.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (ds is null || !ds.ReplacePlaintextWithReference(key, plaintext))
                return false;
            await db.SaveChangesAsync(ct);
            return true;
        }

        var reference = ConnectorSecretReference.CanonicalFor(id, key);
        // 値は全てパラメータで渡す（キー・平文もパラメータ。SQL へ連結しない）。
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "DataSources"
               SET "Config" = jsonb_set("Config", ARRAY[{{key}}]::text[], to_jsonb({{reference}}::text), false)
             WHERE "Id" = {{id}}
               AND "Config" ->> {{key}} = {{plaintext}}
            """, ct);
        return updated == 1;
    }
}
