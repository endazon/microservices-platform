using DataSourceService.Domain;
using DataSourceService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataSourceService.Infrastructure.Secrets;

// NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, [[IADR-0663]] 決定 2 (#458 段 S4): **移送期間の旗を偽にできるのは、平文の行が 0 件のときだけ**。
//
// 旗（`ConnectorCredentials:PlaintextAllowed`）が偽なのに、設定に平文の資格情報を持つデータソースが 1 件でも残っていれば、
// **起動を拒む**（ホストの開始の前に例外）。平文を持つ行を黙って同期の失敗へ倒すと、移送の漏れが「同期が止まった」に化ける。
// 🔴 時刻で切らない（作業仕様書 20261003_458 §窓 1）。数えるのは状態に依らず全行である（無効化した行の平文も保管の対象である）。
// ログに出すのは件数だけ（ID・キー名・値を出さない。件数が分かれば移送のコマンドが行ごとの結果を出す）。
public sealed class PlaintextCredentialStartupGate(
    IServiceScopeFactory scopes,
    IOptions<ConnectorCredentialOptions> options,
    ILogger<PlaintextCredentialStartupGate> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.PlaintextAllowed)
            return;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        var rows = await CountPlaintextRowsAsync(db, cancellationToken);
        if (rows > 0)
        {
            logger.LogCritical(
                "移送期間の旗が偽（ConnectorCredentials:PlaintextAllowed=false）なのに、平文の資格情報を持つデータソースが {Rows} 件ある。起動しない",
                rows);
            throw new InvalidOperationException(
                $"ConnectorCredentials:PlaintextAllowed=false だが、平文の資格情報を持つデータソースが {rows} 件ある。"
                + "先に移送（migrate-connector-credentials）を流すか、旗を true に戻す。");
        }

        logger.LogInformation("移送期間の旗が偽: 平文の資格情報を持つデータソースは 0 件");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // 平文の資格情報を持つデータソースの件数（状態に依らず全行）。表は小さい（管理者が登録する件数）ので、設定を読んで数える。
    internal static async Task<int> CountPlaintextRowsAsync(DataSourceDbContext db, CancellationToken ct)
    {
        var configs = await db.DataSources.AsNoTracking().Select(d => d.Config).ToListAsync(ct);
        return configs.Count(c => PlaintextCredential.KeysOf(c).Count > 0);
    }
}
