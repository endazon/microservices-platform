using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Clustering;
using GraphService.Domain.Ports;
using GraphService.Features.Clustering.Detect;
using GraphService.Features.KnowledgeHealth.Report;
using GraphService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GraphService.Tests.Features.Clustering;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3・6・8, ADR-0083 決定 1〜3, [[IADR-0425]] (#1363):
// クラスタ検出の日次バッチと、`unsummarized-clusters` の生産。
//
// 本ファイルが固定するのは次の 4 点である。
//
//  1. 入力から**個人資料を落とす**（ADR-0035 決定 8。陽性対照つき）
//  2. 再検出でクラスタの**同一性が保たれ**、構成が変わったときだけ時刻が前進する
//  3. 消滅したクラスタは**所属・要約ごと**消える
//  4. 未要約クラスタ数が ADR-0083 決定 3 の 3 条件で数えられ、**リースを取れない周期は走らない**
[Trait("TestKind", "Integration")]
public sealed class ClusterDetectionTests
{
    private static readonly DateTimeOffset Day1 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static readonly DateTimeOffset Day2 = DateTimeOffset.Parse("2026-09-02T00:00:00Z");

    // ── 1. 入力（個人資料の除外） ───────────────────────────────────────────

    // 🔴 FR-17, ADR-0035 決定 8 (T-6): **個人資料は共有グラフに載らない。**
    // 「共有グラフは個人資料を含まない 1 つとする（決定 3 のクラスタリング入力除外と同じ）」。
    // **陽性対照つき** —— 組織文書はちゃんとクラスタに入る（判定を否定形で書くと全部落ちる）。
    [Fact]
    public async Task 個人資料はクラスタの構成員にならない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        var note = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書");
            AddDocument(db, note, "個人メモ",
                new Dictionary<string, string> { [GraphDocumentScope.Key] = GraphDocumentScope.PrivateNote });
            Clique(db, org);
            // 個人資料からクラスタの中心へ辺を張る（それでも入らないことを見る）。
            AddEdge(db, note, org[0]);
        });

        await RunAsync(factory, Day1);

        var members = await MembersAsync(factory);
        members.Should().NotContain(note, "個人資料はクラスタリングの入力から除かれる");
        members.Should().BeEquivalentTo(org, "陽性対照 —— 組織文書は構成員になる");
    }

    // ── 2. 同一性と構成変更の時刻 ───────────────────────────────────────────

    // 🔴 FR-17, ADR-0083 決定 3 条件 2 (T-8): **構成が変わらない再検出では時刻が進まない。**
    // ここが破れると、日次バッチが毎日「構成が変わった」と主張し、
    // 全クラスタが恒久的に未要約になる（指標が使えなくなる）。
    [Fact]
    public async Task 構成が変わらない再検出では構成変更時刻が進まない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書");
            Clique(db, org);
        });

        await RunAsync(factory, Day1);
        var before = await ClustersAsync(factory);

        var second = await RunAsync(factory, Day2);
        var after = await ClustersAsync(factory);

        second.Unchanged.Should().Be(before.Count);
        second.Changed.Should().Be(0);
        after.Select(c => c.ClusterId).Should().BeEquivalentTo(before.Select(c => c.ClusterId),
            "同じ検出結果なら同じクラスタである（同一性が保たれる）");
        after.Should().AllSatisfy(c => c.CompositionChangedAt.Should().Be(Day1));
    }

    // FR-17, ADR-0083 決定 3 条件 2 (T-9): **陽性対照。** 構成が変われば時刻が進み、
    // それでもクラスタ ID は保たれる（過半を共有しているため）。
    [Fact]
    public async Task 構成が変われば同じクラスタの構成変更時刻が進む()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        var joined = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書");
            Clique(db, org);
        });

        await RunAsync(factory, Day1);
        var before = await ClustersAsync(factory);

        // 5 件目を塊へ引き込む（4 件すべてと繋ぐ）。
        await SeedAsync(factory, db =>
        {
            AddDocument(db, joined, "後から来た文書");
            foreach (var id in org)
                AddEdge(db, joined, id);
        });
        var second = await RunAsync(factory, Day2);
        var after = await ClustersAsync(factory);

        second.Changed.Should().Be(1);
        after.Should().ContainSingle().Which.ClusterId.Should().Be(before.Single().ClusterId,
            "過半を共有するので同じクラスタである（要約が別物へ引き継がれない）");
        after.Single().CompositionChangedAt.Should().Be(Day2);
        (await MembersAsync(factory)).Should().Contain(joined);
    }

    // ── 3. 消滅 ────────────────────────────────────────────────────────────

    // FR-17 (T-10f): 消滅したクラスタは**所属も要約も**残さない。
    // 孤児が残ると、未要約クラスタ数の分母に存在しないクラスタが混ざる。
    [Fact]
    public async Task 消滅したクラスタは所属も要約も残さない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書");
            Clique(db, org);
        });
        await RunAsync(factory, Day1);
        await SeedSummariesAsync(factory, Day1);

        // 文書がすべて消えた（DocumentDeleted の購読が消したのと同じ状態）。
        await SeedAsync(factory, db => db.Documents.RemoveRange(db.Documents));
        await RunAsync(factory, Day2);

        (await ClustersAsync(factory)).Should().BeEmpty();
        (await MembersAsync(factory)).Should().BeEmpty();
        await using var scope = factory.Services.CreateAsyncScope();
        var db2 = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
        (await db2.ClusterSummaries.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(0, "クラスタが消えれば、その要約の生成時刻も残らない");
    }

    // ── 4. 生産者とリース ──────────────────────────────────────────────────

    // 🔴 FR-10, FR-18, ADR-0083 決定 3 条件 1 (T-11): 要約が 1 件も無いので、
    // **検出された全クラスタが未要約**である。**これは 0 件ではなく実測値である** ——
    // 計画は「生産者の無い指標を 0 件として並べてはならない」と定めており、
    // #1363 が塞いだのはまさにその状態である。
    [Fact]
    public async Task 要約が無ければ全クラスタが未要約として報告される()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var left = Ids(4);
        var right = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in left.Concat(right))
                AddDocument(db, id, "組織文書");
            Clique(db, left);
            Clique(db, right);
        });
        await RunAsync(factory, Day1);

        var observed = await CollectUnsummarizedAsync(factory);

        observed.Should().HaveCount(2);
        observed.Should().AllSatisfy(o =>
        {
            o.Dimension.Should().Be(UnsummarizedClusterRule.NoSummary);
            // 🔴 クラスタリングの入力から個人資料を除いてあるので、構造的に混ざらない。
            o.DocScope.Should().BeNull();
        });
    }

    // 🔴 FR-10, FR-18 (T-15f): **陰性対照。** 4 通りの要約が揃い、構成変更も文書更新も
    // 生成より前なら、そのクラスタは**1 件も報告されない**。
    // これが無いと「常に全クラスタを報告する実装」でも上のテストは緑になる。
    [Fact]
    public async Task 四通りの要約が揃ったクラスタは報告されない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書", updatedAt: Day1);
            Clique(db, org);
        });
        await RunAsync(factory, Day1);
        // 検出より後に 4 通りぶん生成した。
        await SeedSummariesAsync(factory, Day2);

        (await CollectUnsummarizedAsync(factory)).Should().BeEmpty(
            "🔴 これが無いと「常に未要約」の実装でも他のテストが全部緑になる");
    }

    // FR-10 (T-16): クラスタが 1 つも無ければ観測値は空である（0 件でも報告は送られる ——
    // 送出そのものは KnowledgeHealthProducerTests が固定する）。
    [Fact]
    public async Task クラスタが無ければ観測値は空である()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();

        (await CollectUnsummarizedAsync(factory)).Should().BeEmpty();
    }

    // FR-17, [[IADR-0425]] 決定 6 (T-18): 🔴 **リースを取得できない周期は検出しない。**
    // 2 レプリカが同時に走ると、互いのクラスタを「対応がつかない＝消滅」と判定して消し合う。
    [Fact]
    public async Task リースを取得できない周期は検出しない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書");
            Clique(db, org);
        });

        var worker = new ClusterDetectionHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new DenyingCoordinator(),
            NullLogger<ClusterDetectionHostedService>.Instance);

        var ran = await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        ran.Should().BeFalse();
        (await ClustersAsync(factory)).Should().BeEmpty("スキップした周期は 1 行も書かない");
    }

    // FR-17 (T-18b): **陽性対照。** リースを取得できた周期は検出する。
    // これが無いと、上のテストは「そもそも一度も検出しない実装」でも緑になる。
    [Fact]
    public async Task リースを取得できた周期は検出する()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書");
            Clique(db, org);
        });

        var worker = new ClusterDetectionHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new GrantingCoordinator(),
            NullLogger<ClusterDetectionHostedService>.Instance);

        var ran = await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        ran.Should().BeTrue();
        (await ClustersAsync(factory)).Should().ContainSingle();
    }

    // FR-17, ADR-0035 決定 3 (T-18c): 周期は**日次**である（計画が定めた実行タイミング）。
    [Fact]
    public void 検出の周期は日次である()
        => ClusterDetectionHostedService.Interval.Should().Be(TimeSpan.FromDays(1));

    // ── 器 ─────────────────────────────────────────────────────────────────

    private static Guid[] Ids(int count) => [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid())];

    private static Task SeedAsync(TestWebApplicationFactory factory, Action<GraphDbContext> seed)
        => factory.SeedAsync(db =>
        {
            seed(db);
            return Task.CompletedTask;
        });

    private static void AddDocument(
        GraphDbContext db, Guid id, string title,
        Dictionary<string, string>? attributes = null, DateTimeOffset? updatedAt = null)
        => db.Documents.Add(GraphDocument.Create(
            id, title, attributes ?? [], bodyHash: null, updatedAt ?? DateTimeOffset.UnixEpoch));

    // 辺の型は起動時の seed（EdgeTypeSeed）で既に入っている。**新しく足さない。**
    private static void AddEdge(GraphDbContext db, Guid source, Guid target)
    {
        var type = db.EdgeTypes.First();
        db.Edges.Add(Edge.Create(source, target, type.Id, type.IsSymmetric, EdgeProvenance.Auto));
    }

    private static void Clique(GraphDbContext db, IReadOnlyList<Guid> ids)
    {
        for (var i = 0; i < ids.Count; i++)
            for (var j = i + 1; j < ids.Count; j++)
                AddEdge(db, ids[i], ids[j]);
    }

    private static async Task<ClusterDetectionResult> RunAsync(
        TestWebApplicationFactory factory, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var job = new ClusterDetectionJob(
            scope.ServiceProvider.GetRequiredService<GraphDbContext>(),
            new FixedClock(now),
            NullLogger<ClusterDetectionJob>.Instance);
        return await job.RunAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<GraphCluster>> ClustersAsync(TestWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GraphDbContext>()
            .Clusters.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<Guid>> MembersAsync(TestWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GraphDbContext>()
            .ClusterMembers.AsNoTracking().Select(m => m.DocumentId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    // 機密区分 4 通りぶんの要約を、全クラスタへ入れる（要約生成バッチの代わり）。
    private static async Task SeedSummariesAsync(
        TestWebApplicationFactory factory, DateTimeOffset generatedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GraphDbContext>();
        var ids = await db.Clusters.AsNoTracking().Select(c => c.ClusterId)
            .ToListAsync(TestContext.Current.CancellationToken);
        foreach (var id in ids)
            foreach (var confidentiality in ClusterConfidentiality.All)
                db.ClusterSummaries.Add(
                    GraphClusterSummary.Create(id, confidentiality, generatedAt));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectUnsummarizedAsync(
        TestWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var collector = scope.ServiceProvider.GetRequiredService<KnowledgeHealthCollector>();
        return await collector.CollectUnsummarizedClustersAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DenyingCoordinator : IClusterDetectionLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class GrantingCoordinator : IClusterDetectionLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(new Lease());

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
