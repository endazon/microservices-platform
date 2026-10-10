using System.Data.Common;
using System.Diagnostics;
using AwesomeAssertions;
using FluentValidation;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
// 名前空間 Knowledge.IntegrationTests.DashboardService の末尾セグメントと衝突するため global:: で明示する。
using global::DashboardService.Domain;
using global::DashboardService.Features.KnowledgeHealth.Report;
using global::DashboardService.Infrastructure.Persistence;

namespace Knowledge.IntegrationTests.DashboardService;

// FR-10, FR-17, FR-18, NFR-16, SC-10 (#1895): ナレッジ健全性の観測値のスナップショット置換を**実 PostgreSQL で**測る。
//
// 稼働の PoC で、graph → dashboard の `orphan-documents`（38,703 件）の報告が毎周期 gRPC の期限（5 秒）を超えた。
// 受け口は 1 行ずつ DELETE / INSERT しており、期限で取り消されてロールバックしていた。ここで固定するのは次の 3 点である。
//   1. **往復の数が件数に比例しない**（10 件と 40,000 件で同じ数の SQL 文）。4 万件が期限 5 秒に収まる。
//   2. **置換の途中は読み手に見えない**（DELETE の後・INSERT の後の時点で、別の接続からは旧い集合だけが見える）。
//   3. **途中で取り消されたら旧い集合が残る**（期限切れは取り消しとして届く。半端な状態でコミットしない）。
//
// **単体テストでは踏めない経路である。** `DashboardService.Tests` は EF InMemory であり、InMemory は
// `ExecuteDeleteAsync`・生 SQL・トランザクションのいずれも持たない（受け口は InMemory のとき従前の経路へ分岐する）。
[Trait("Category", "Integration")]
public sealed class KnowledgeHealthSnapshotPostgresTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 稼働で期限切れになった件数（38,703）を上回る規模。
    private const int LargeCount = 40_000;

    // 送信側（GraphService の `GrpcKnowledgeHealthReporter.SendTimeout`）の期限。サービスを跨ぐため定数を共有できない。
    private static readonly TimeSpan SenderDeadline = TimeSpan.FromSeconds(5);

    // NFR-16 (#1895): 往復の数が件数に比例しない。4 万件の置換が送信側の期限に収まる。
    [Fact]
    public async Task 四万件の置換でもSQL文の数は十件のときと同じで期限内に終わる()
    {
        var cs = await DatabaseAsync();

        // 置換される側の旧い集合も 4 万件を置く（従前の経路は、旧い行の数だけ DELETE を流していた）。
        await ReportAsync(cs, Request(KnowledgeHealthIndicators.OrphanDocuments, LargeCount, "old"));

        var small = await CountCommandsAsync(cs, Request(KnowledgeHealthIndicators.UnresolvedLinks, 10, "s"));

        var counter = new CommandCounter();
        var sw = Stopwatch.StartNew();
        var outcome = await ReportAsync(cs, Request(KnowledgeHealthIndicators.OrphanDocuments, LargeCount, "new"), counter);
        sw.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"40,000 件の置換: {sw.ElapsedMilliseconds} ms, SQL 文 {counter.Count} 本（10 件のとき {small} 本）");

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Accepted.Should().Be(LargeCount);
        counter.Count.Should().Be(small, "SQL 文の数は観測値の件数に比例してはならない");
        counter.Count.Should().BeLessThanOrEqualTo(5,
            "DELETE 1・INSERT 1・しきい値の読み 1・しきい値の書き 0〜1 で足りる");
        sw.Elapsed.Should().BeLessThan(SenderDeadline, "送信側の期限（5 秒）に収まらなければ毎周期ロールバックする");

        await using var db = NewContext(cs);
        (await db.KnowledgeHealthObservations.CountAsync(
            o => o.Indicator == KnowledgeHealthIndicators.OrphanDocuments, Ct)).Should().Be(LargeCount);
        (await db.KnowledgeHealthObservations.CountAsync(
            o => o.Indicator == KnowledgeHealthIndicators.OrphanDocuments && o.SubjectKey.StartsWith("old-"), Ct))
            .Should().Be(0, "旧い集合は 1 行も残らない");
    }

    // FR-10, NFR-16 (#1895): 置換の途中（DELETE の後・INSERT の後）は、別の接続からは旧い集合だけが見える。
    [Fact]
    public async Task 置換の途中は別の接続から旧い集合だけが見える()
    {
        var cs = await DatabaseAsync();
        await ReportAsync(cs, Request(KnowledgeHealthIndicators.OrphanDocuments, 3, "old"));

        var seen = new List<(string Command, long Total, long Old)>();
        var probe = new CommandCounter
        {
            AfterEach = async text =>
            {
                if (!text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                    && !text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
                    return;
                seen.Add((text[..6], await CountFromOutsideAsync(cs, null), await CountFromOutsideAsync(cs, "old-")));
            },
        };

        var outcome = await ReportAsync(cs, Request(KnowledgeHealthIndicators.OrphanDocuments, 5, "new"), probe);

        outcome.IsSuccess.Should().BeTrue();
        seen.Select(s => s.Command.ToUpperInvariant()).Should().Equal("DELETE", "INSERT");
        seen.Should().AllSatisfy(s =>
        {
            s.Total.Should().Be(3, "コミット前は旧い集合の件数のまま");
            s.Old.Should().Be(3, "コミット前は旧い集合そのもの");
        });
        (await CountFromOutsideAsync(cs, null)).Should().Be(5);
        (await CountFromOutsideAsync(cs, "new-")).Should().Be(5);
    }

    // FR-10, NFR-16 (#1895): 期限切れ（gRPC は取り消しとして届く）で途中で止まったら、旧い集合としきい値がそのまま残る。
    [Fact]
    public async Task 挿入の後で取り消されたら旧い集合としきい値が残る()
    {
        var cs = await DatabaseAsync();
        await ReportAsync(cs, Request(KnowledgeHealthIndicators.StaleDocuments, 3, "old", thresholdDays: 180));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var probe = new CommandCounter
        {
            AfterEach = text =>
            {
                if (text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
                    deadline.Cancel();
                return Task.CompletedTask;
            },
        };

        var act = () => ReportAsync(
            cs, Request(KnowledgeHealthIndicators.StaleDocuments, 7, "new", thresholdDays: 90), probe, deadline.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await using var db = NewContext(cs);
        var rows = await db.KnowledgeHealthObservations.AsNoTracking()
            .Where(o => o.Indicator == KnowledgeHealthIndicators.StaleDocuments).ToListAsync(Ct);
        rows.Should().HaveCount(3).And.OnlyContain(o => o.SubjectKey.StartsWith("old-"));
        (await db.KnowledgeHealthIndicatorThresholds.AsNoTracking()
            .SingleAsync(t => t.Indicator == KnowledgeHealthIndicators.StaleDocuments, Ct)).ThresholdDays.Should().Be(180);
    }

    // FR-10, FR-17, FR-18 (#1895): 一括の経路でも、置換は指標単位で、値（null の区別・軸・切り詰め）は従前と同じに保存される。
    [Fact]
    public async Task 一括の経路でも指標単位の置換と値の保存は従前と同じである()
    {
        var cs = await DatabaseAsync();
        await ReportAsync(cs, Request(KnowledgeHealthIndicators.UnresolvedLinks, 2, "keep"));
        await ReportAsync(cs, Request(KnowledgeHealthIndicators.EdgeTypeUsage, 4, "old", thresholdDays: 30));

        var longKey = new string('k', KnowledgeHealthObservation.MaxSubjectKeyLength + 10);
        var outcome = await ReportAsync(cs, new KnowledgeHealthReportRequest(
            "EDGE-TYPE-USAGE",
            [
                new(longKey, " Private-Note ", " references "),
                new("plain"),
                new("   "),
            ]));

        outcome.IsSuccess.Should().BeTrue();
        outcome.Value.Should().Be(new ReportKnowledgeHealthOutcome(KnowledgeHealthIndicators.EdgeTypeUsage, 2));
        await using var db = NewContext(cs);
        var rows = await db.KnowledgeHealthObservations.AsNoTracking()
            .Where(o => o.Indicator == KnowledgeHealthIndicators.EdgeTypeUsage).OrderBy(o => o.SubjectKey).ToListAsync(Ct);
        rows.Should().HaveCount(2);
        rows[0].SubjectKey.Should().Be(new string('k', KnowledgeHealthObservation.MaxSubjectKeyLength));
        rows[0].DocScope.Should().Be(KnowledgeDocScopes.PrivateNote);
        rows[0].Dimension.Should().Be("references");
        rows[1].SubjectKey.Should().Be("plain");
        rows[1].DocScope.Should().BeNull();
        rows[1].Dimension.Should().BeNull();
        rows.Select(r => r.Id).Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
        (await db.KnowledgeHealthObservations.CountAsync(o => o.Indicator == KnowledgeHealthIndicators.UnresolvedLinks, Ct))
            .Should().Be(2, "他の指標は置換されない");
        (await db.KnowledgeHealthIndicatorThresholds.AnyAsync(
            t => t.Indicator == KnowledgeHealthIndicators.EdgeTypeUsage, Ct)).Should().BeFalse("しきい値を添えない報告は行を消す");
    }

    private static KnowledgeHealthReportRequest Request(
        string indicator, int count, string prefix, int? thresholdDays = null)
        => new(
            indicator,
            [.. Enumerable.Range(0, count).Select(i => new KnowledgeHealthObservationRequest(
                $"{prefix}-{i:D6}-{Guid.NewGuid():N}", i % 7 == 0 ? KnowledgeDocScopes.PrivateNote : null))],
            thresholdDays);

    private static async Task<Platform.Shared.Kernel.Result<ReportKnowledgeHealthOutcome>> ReportAsync(
        string cs, KnowledgeHealthReportRequest request, CommandCounter? counter = null, CancellationToken? ct = null)
    {
        await using var db = NewContext(cs, counter);
        // 入力規則は DashboardService.Tests が測る。ここは保存の経路だけを見るので、規則の無い検証器を渡す。
        var useCase = new ReportKnowledgeHealthUseCase(db, new InlineValidator<KnowledgeHealthReportRequest>());
        return await useCase.ExecuteAsync(request, ct ?? Ct);
    }

    private static async Task<int> CountCommandsAsync(string cs, KnowledgeHealthReportRequest request)
    {
        var counter = new CommandCounter();
        (await ReportAsync(cs, request, counter)).IsSuccess.Should().BeTrue();
        return counter.Count;
    }

    // 置換を走らせている接続とは**別の接続**で数える（同じ接続ではコミット前の行が見えてしまう）。
    private static async Task<long> CountFromOutsideAsync(string cs, string? subjectPrefix)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM \"KnowledgeHealthObservations\" WHERE \"Indicator\" = @i"
            + (subjectPrefix is null ? "" : " AND \"SubjectKey\" LIKE @p"), conn);
        cmd.Parameters.AddWithValue("i", KnowledgeHealthIndicators.OrphanDocuments);
        if (subjectPrefix is not null) cmd.Parameters.AddWithValue("p", subjectPrefix + "%");
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string> DatabaseAsync()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        postgres.IsAvailable.Should().BeTrue("門を通ったのに PostgreSQL を得られない");
        var name = $"dashhealth_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(postgres.ConnectionString!);
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString!))
        {
            await admin.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        builder.Database = name;
        // 本番と同じくマイグレーションで作る。
        await using (var db = NewContext(builder.ConnectionString))
            await db.GetService<IMigrator>().MigrateAsync(cancellationToken: Ct);
        return builder.ConnectionString;
    }

    private static DashboardDbContext NewContext(string cs, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<DashboardDbContext>().UseNpgsql(cs);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new DashboardDbContext(options.Options);
    }

    // EF が送った SQL 文を数える。`AfterEach` は文の実行直後（コミット前）に呼ばれる。
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public Func<string, Task>? AfterEach { get; init; }

        private async Task ObserveAsync(DbCommand command)
        {
            Count++;
            if (AfterEach is not null) await AfterEach(command.CommandText.TrimStart());
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command);
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command);
            return result;
        }

        public override async ValueTask<object?> ScalarExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
        {
            await ObserveAsync(command);
            return result;
        }
    }
}
