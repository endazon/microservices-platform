using DataSourceService.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace DataSourceService.Infrastructure.Secrets.Migration;

// NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, [[IADR-0664]] 決定 3・4 (#458 段 S4): 移送の**入口**（`dotnet DataSourceService.dll migrate-connector-credentials`）。
//
// 稼働の Pod の中で `kubectl exec -i` で流す（接続文字列・Vault の所在は Pod の環境から読む。手順は scripts/migrate-datasource-credentials.sh）。
//   migrate-connector-credentials --dry-run        平文を持つ行とキーを数えるだけ（Vault にも DB にも書かない。トークン不要）
//   migrate-connector-credentials --token-stdin    標準入力の 1 行目を Vault のトークンとして読み、移送する
// 🔴 トークンは引数・環境変数で受け取らない（プロセスの一覧・Pod の定義・シェルの履歴に残さない）。標準入力だけ。
// 終了コード: 0 = 全部移した（または移すものが無い）、1 = 衝突・失敗が残った（その行は平文のまま）、2 = 使い方・構成の誤り。
public static class ConnectorCredentialMigrationCommand
{
    public const string Verb = "migrate-connector-credentials";

    public static bool IsInvoked(string[] args) => args.Length > 0 && args[0] == Verb;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, TextReader stdin, TextWriter stdout, CancellationToken ct)
    {
        var rest = args.Skip(1).ToArray();
        var dryRun = rest.Contains("--dry-run");
        var tokenStdin = rest.Contains("--token-stdin");
        var unknown = rest.Where(a => a is not ("--dry-run" or "--token-stdin")).ToArray();
        if (unknown.Length > 0 || (dryRun == tokenStdin))
        {
            await stdout.WriteLineAsync($"使い方: {Verb} --dry-run | {Verb} --token-stdin（トークンは標準入力の 1 行目）");
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DataSourceDbContext>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        var vaultOptions = scope.ServiceProvider.GetRequiredService<IOptions<VaultConnectorSecretOptions>>().Value;

        MigrationVaultKvClient vault;
        HttpClient? http = null;
        if (dryRun)
        {
            // dry-run は Vault を呼ばない（呼ぶ経路に入らない）。器だけ作る。
            vault = new MigrationVaultKvClient(new HttpClient(), token: string.Empty, vaultOptions.KvMount);
        }
        else
        {
            if (!vaultOptions.IsConfigured)
            {
                await stdout.WriteLineAsync("Vault:Address が未設定である（services.datasource.vault.address）。移送先が無いので止める。");
                return 2;
            }

            var token = (await stdin.ReadLineAsync(ct))?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                await stdout.WriteLineAsync("標準入力にトークンが無い。");
                return 2;
            }

            http = new HttpClient
            {
                BaseAddress = new Uri(vaultOptions.Address!.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(Math.Max(1, vaultOptions.TimeoutSeconds)),
            };
            vault = new MigrationVaultKvClient(http, token, vaultOptions.KvMount);
        }

        try
        {
            var migrator = new ConnectorCredentialMigrator(db, vault, loggerFactory.CreateLogger<ConnectorCredentialMigrator>());
            var report = await migrator.RunAsync(dryRun, ct);
            var remaining = await PlaintextCredentialStartupGate.CountPlaintextRowsAsync(db, ct);
            await stdout.WriteLineAsync(
                $"{Verb}{(dryRun ? "（dry-run）" : string.Empty)}: 平文を持つ行 {report.Rows}・平文のキー {report.PlaintextKeys}・"
                + $"移した {report.Migrated}・衝突 {report.Conflicts}・失敗 {report.Failed}・平文が残る行 {remaining}");
            if (!dryRun && remaining == 0)
                await stdout.WriteLineAsync("平文の行は 0 件。旗（services.datasource.connectorCredentials.plaintextAllowed）を false にできる。");
            return dryRun || report.Succeeded ? 0 : 1;
        }
        finally
        {
            http?.Dispose();
        }
    }
}
