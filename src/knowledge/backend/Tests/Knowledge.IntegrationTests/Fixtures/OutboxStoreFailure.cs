using Npgsql;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Knowledge.IntegrationTests.Fixtures;

// FR-12, ADR-0027 §決定, IADR-0657 (#2361): **送出表（Outbox）への書き込みを失敗させる**注入。
//
// 渡した接続文字列の DB の `wolverine.wolverine_outgoing_envelopes` に、INSERT を例外で止めるトリガを置く。
// 🔴 **試験ごとの専用の DB にだけ使う**（共有の DB に置くと、並列に走る他の試験の送出まで止める）。
// 共有のロールの権限（REVOKE / GRANT）には触れない —— トリガは置いた DB の中だけで効き、DROP で跡が残らない。
// 🔴 **片付けは呼び出し側の `finally` で `RemoveAsync` を必ず呼ぶ**（試験が途中で落ちてもトリガを残さない）。
public static class OutboxStoreFailure
{
    private const string Function = "test_reject_outgoing_envelopes";
    private const string Trigger = "test_reject_outgoing_envelopes";
    public const string Message = "試験の注入: 送出表への書き込みを拒否した";

    private static string Table => $"{WolverineExtensions.OutboxSchemaName}.wolverine_outgoing_envelopes";

    public static async Task InstallAsync(string connectionString, CancellationToken ct)
    {
        await ExecuteAsync(connectionString,
            $"CREATE OR REPLACE FUNCTION public.{Function}() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + $"BEGIN RAISE EXCEPTION '{Message}'; END $$; "
            + $"DROP TRIGGER IF EXISTS {Trigger} ON {Table}; "
            + $"CREATE TRIGGER {Trigger} BEFORE INSERT ON {Table} FOR EACH ROW EXECUTE FUNCTION public.{Function}();",
            ct);
    }

    // 試験の取り消し（ct）で片付けまで止まらないよう、片付けは取り消しを受けない。
    public static Task RemoveAsync(string connectionString) =>
        ExecuteAsync(connectionString,
            $"DROP TRIGGER IF EXISTS {Trigger} ON {Table}; DROP FUNCTION IF EXISTS public.{Function}();",
            CancellationToken.None);

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
