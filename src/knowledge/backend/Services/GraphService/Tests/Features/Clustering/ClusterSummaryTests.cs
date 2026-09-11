using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Clustering;
using GraphService.Domain.Ports;
using GraphService.Features.Clustering.Detect;
using GraphService.Features.Clustering.Summarize;
using GraphService.Features.KnowledgeHealth.Report;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GraphService.Tests.Features.Clustering;

// FR-17, FR-18, SC-10, SC-18, ADR-0035 決定 3・5・6・8, ADR-0051 決定 4, ADR-0083 決定 2・3,
// [[IADR-0430]] (#1395): **クラスタ要約の生成バッチ。**
//
// 本ファイルが固定するのは次の 5 点である。
//
//  1. 🔴 **生成すると `unsummarized-clusters` が減る**（陽性）。減らない陰性対照つき
//  2. 🔴 **私的ノートは送信本文に入らない**（陰性対照。構成員に紛れ込ませても封が落とす）
//  3. 🔴 **1 区分でも採れなければ 1 行も書かない**（fail-safe。部分書き込みの禁止）
//  4. 要約の**本文は `graph_cluster_summaries` に入らない**（ADR-0035 決定 5 / [[IADR-0425]] 決定 4）
//  5. 1 周期の上限・選ぶ順序・リース・**既定オフ**
[Trait("TestKind", "Integration")]
public sealed class ClusterSummaryTests
{
    private static readonly DateTimeOffset Day1 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static readonly DateTimeOffset Day2 = DateTimeOffset.Parse("2026-09-02T00:00:00Z");
    private static readonly DateTimeOffset Day3 = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
    private static readonly DateTimeOffset Day4 = DateTimeOffset.Parse("2026-09-04T00:00:00Z");

    // ── 1. 陽性と陰性対照 ───────────────────────────────────────────────────

    // 🔴 FR-18, ADR-0083 決定 2・3 (T-1): **生成すると未要約クラスタ数が減る。**
    // これが #1395 の主張そのものである —— 書き手が居なかったので指標は常に全クラスタを返していた。
    [Fact]
    public async Task 要約を生成すると未要約クラスタ数が減る()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書", ConfidentialityLevels.Internal);
            Clique(db, org);
        });
        await DetectAsync(factory, Day1);

        (await CollectUnsummarizedAsync(factory)).Should().ContainSingle(
            "要約が 1 つも無いので条件 1 に当たる");

        var llm = new StubLlm();
        var result = await SummarizeAsync(factory, Day2, llm);

        result.Generated.Should().Be(1);
        result.Failed.Should().Be(0);
        (await CollectUnsummarizedAsync(factory)).Should().BeEmpty(
            "🔴 機密区分 4 通りが揃い、構成変更も文書更新も生成より前になった");
        (await SummaryStampsAsync(factory)).Should().HaveCount(4,
            "ADR-0083 決定 2 —— 単位はクラスタ × 機密区分（4 通り）である");
    }

    // 🔴 **陰性対照。** 生成を挟まなければ減らない。
    // これが無いと「常に 0 件を返す収集器」でも上のテストが緑になる。
    [Fact]
    public async Task 生成しなければ未要約クラスタ数は減らない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書", ConfidentialityLevels.Internal);
            Clique(db, org);
        });
        await DetectAsync(factory, Day1);

        (await CollectUnsummarizedAsync(factory)).Should().ContainSingle();
        (await SummaryStampsAsync(factory)).Should().BeEmpty();
    }

    // ── 2. 私的ノート（陰性対照） ──────────────────────────────────────────

    // 🔴 FR-17, ADR-0035 決定 8 (T-3): **私的ノートは送信本文に入らない。**
    //
    // 検出の入力から既に除いてあるので、**構成員の表へ直接ねじ込んでから**試す ——
    // 「検出が落としているから安全」ではなく「**封が落とすから安全**」であることを測る
    // （多層防御。迂回経路が生まれても出口で濾される）。**陽性対照つき。**
    [Fact]
    public async Task 私的ノートは要約の送信本文に入らない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        var note = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書", ConfidentialityLevels.Internal);
            Clique(db, org);
            AddDocument(db, note, "極秘の個人メモ", ConfidentialityLevels.Internal, privateNote: true);
        });
        await DetectAsync(factory, Day1);

        // 🔴 検出の外側から構成員へねじ込む（迂回経路の再現）。
        await SeedAsync(factory, db =>
        {
            var clusterId = db.Clusters.AsNoTracking().Select(c => c.ClusterId).Single();
            db.ClusterMembers.Add(GraphClusterMember.Create(clusterId, note));
        });

        var llm = new StubLlm();
        await SummarizeAsync(factory, Day2, llm);

        llm.Prompts.Should().NotBeEmpty("陽性対照 —— 要約の呼び出し自体は起きている");
        llm.Prompts.SelectMany(p => p.Members).Select(m => m.DocumentId)
            .Should().NotContain(note, "ADR-0035 決定 8 —— 共有グラフは個人資料を含まない");
        llm.Prompts.Select(p => p.Render()).Should().AllSatisfy(
            text => text.Should().NotContain("極秘の個人メモ"));
        llm.Prompts.SelectMany(p => p.Members).Select(m => m.DocumentId)
            .Should().Contain(org[0], "陽性対照 —— 組織文書は入る");
    }

    // ── 3. fail-safe（部分書き込みの禁止） ─────────────────────────────────

    // 🔴 FR-18, ADR-0035 決定 6, ADR-0083 決定 2 (T-6): **1 区分でも採れなければ 1 行も書かない。**
    //
    // 区分 `public` は成功し、`internal` 以上が失敗する形を作る。**成功した区分の行も残ってはならない** ——
    // 部分的に書くと「条件 1 は満たすのに中身が古い」区別のつかない状態が生まれる。
    [Fact]
    public async Task 一区分でも採れなければそのクラスタは一行も書かれない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var open = Ids(2);
        var inner = Ids(2);
        await SeedAsync(factory, db =>
        {
            foreach (var id in open)
                AddDocument(db, id, "公開資料", ConfidentialityLevels.Public);
            foreach (var id in inner)
                AddDocument(db, id, "社内資料", ConfidentialityLevels.Internal);
            Clique(db, [.. open, .. inner]);
        });
        await DetectAsync(factory, Day1);

        // 公開の 2 件だけの封は成功し、4 件の封（internal 以上）は失敗する。
        var llm = new StubLlm(p => p.Members.Count == 2 ? "公開ぶんの要約" : null);
        var result = await SummarizeAsync(factory, Day2, llm);

        result.Generated.Should().Be(0);
        result.Failed.Should().Be(1);
        (await SummaryStampsAsync(factory)).Should().BeEmpty(
            "🔴 成功した public の行も書かない（部分書き込みの禁止）");
        (await SummaryBodiesAsync(factory)).Should().BeEmpty();
        (await CollectUnsummarizedAsync(factory)).Should().ContainSingle(
            "採れなかったクラスタは未要約のまま残る");
    }

    // (T-7): 全滅（不達）でも例外を投げず、0 件で終わる（周期はスキップされるだけである）。
    [Fact]
    public async Task 全区分で採れなくても例外にならない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書", ConfidentialityLevels.Internal);
            Clique(db, org);
        });
        await DetectAsync(factory, Day1);

        var result = await SummarizeAsync(factory, Day2, new StubLlm(_ => null));

        result.Generated.Should().Be(0);
        result.Failed.Should().Be(1);
    }

    // ── 4. 本文の置き場 ────────────────────────────────────────────────────

    // 🔴 ADR-0035 決定 5, [[IADR-0425]] 決定 4, [[IADR-0430]] 決定 3 (T-16):
    // **本文は `graph_cluster_summaries` に入らない。** 同表は「生成時刻だけを持つ」として
    // 凍結されており、本文は別コレクション（`graph_cluster_summary_bodies`）が持つ。
    [Fact]
    public async Task 要約の本文は生成時刻の表ではなく別の表に入る()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書", ConfidentialityLevels.Internal);
            Clique(db, org);
        });
        await DetectAsync(factory, Day1);
        await SummarizeAsync(factory, Day2, new StubLlm());

        typeof(GraphClusterSummary).GetProperties().Select(p => p.Name)
            .Should().NotContain("Body",
                "[[IADR-0425]] 決定 4 —— graph_cluster_summaries は生成時刻だけを持つ");
        var bodies = await SummaryBodiesAsync(factory);
        bodies.Should().HaveCount(4);
        bodies.Where(b => b.Confidentiality != ClusterConfidentiality.Public)
            .Should().AllSatisfy(b => b.Body.Should().NotBeEmpty());
        // 区分 public から見える文書が 1 件も無いクラスタでは、public の要約は空である
        // （LLM を呼ばずに 4 通りを揃える。[[IADR-0430]] 決定 8）。
        bodies.Single(b => b.Confidentiality == ClusterConfidentiality.Public)
            .Body.Should().BeEmpty();
    }

    // 🔴 [[IADR-0430]] 決定 7: **同じ入力集合には LLM を 1 回しか呼ばない。**
    // 全文書が `internal` なら internal / confidential / restricted の入力は同一であり、
    // 3 回問うても内容は同じで費用だけが 3 倍になる。**行は 4 通りぶん書く。**
    [Fact]
    public async Task 入力集合が同じ区分では呼び出しをまとめる()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書", ConfidentialityLevels.Internal);
            Clique(db, org);
        });
        await DetectAsync(factory, Day1);

        var llm = new StubLlm();
        var result = await SummarizeAsync(factory, Day2, llm);

        result.LlmCalls.Should().Be(1, "public は空・残り 3 区分は同じ入力集合である");
        llm.Prompts.Should().ContainSingle();
        (await SummaryStampsAsync(factory)).Should().HaveCount(4, "それでも行は 4 通り書く");
    }

    // ── 5. 選び方・上限・作り直し ──────────────────────────────────────────

    // (T-10 / T-11): 1 周期の上限が効き、選ぶ順序は `ClusterId` の昇順で決定的である。
    [Fact]
    public async Task 一周期の上限が効きクラスタIDの昇順で選ぶ()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var a = Ids(3);
        var b = Ids(3);
        var c = Ids(3);
        await SeedAsync(factory, db =>
        {
            foreach (var group in new[] { a, b, c })
            {
                foreach (var id in group)
                    AddDocument(db, id, "組織文書", ConfidentialityLevels.Internal);
                Clique(db, group);
            }
        });
        await DetectAsync(factory, Day1);

        var all = (await ClusterIdsAsync(factory)).Order().ToList();
        all.Should().HaveCount(3, "連結成分が 3 つあるので 3 クラスタである");

        var result = await SummarizeAsync(factory, Day2, new StubLlm(), maxClustersPerRun: 2);

        result.Generated.Should().Be(2);
        (await SummaryStampsAsync(factory)).Select(s => s.ClusterId).Distinct().Order()
            .Should().Equal(all.Take(2), "🔴 昇順で先頭から採る（順番待ちが永久に回ってこない形にしない）");
    }

    // (T-12): 要約済みのクラスタは作り直さない（無駄な呼び出しをしない）。
    [Fact]
    public async Task 要約済みのクラスタは作り直さない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書", ConfidentialityLevels.Internal);
            Clique(db, org);
        });
        await DetectAsync(factory, Day1);
        await SummarizeAsync(factory, Day2, new StubLlm());

        var second = new StubLlm();
        var result = await SummarizeAsync(factory, Day3, second);

        result.Candidates.Should().Be(0);
        second.Prompts.Should().BeEmpty("ADR-0035 決定 6 —— 変更のあったクラスタだけを再生成する");
    }

    // 🔴 (T-13): **構成が変わったクラスタは作り直す**（ADR-0083 決定 3 条件 2）。
    [Fact]
    public async Task 構成が変わったクラスタは作り直す()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var org = Ids(4);
        var joined = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            foreach (var id in org)
                AddDocument(db, id, "組織文書", ConfidentialityLevels.Internal);
            Clique(db, org);
        });
        await DetectAsync(factory, Day1);
        await SummarizeAsync(factory, Day2, new StubLlm());

        await SeedAsync(factory, db =>
        {
            AddDocument(db, joined, "後から来た文書", ConfidentialityLevels.Internal);
            foreach (var id in org)
                AddEdge(db, joined, id);
        });
        await DetectAsync(factory, Day3);
        (await CollectUnsummarizedAsync(factory)).Should().ContainSingle(
            "構成変更が最終生成より後なので条件 2 に当たる");

        var result = await SummarizeAsync(factory, Day4, new StubLlm());

        result.Generated.Should().Be(1);
        (await SummaryStampsAsync(factory)).Should().AllSatisfy(
            s => s.GeneratedAt.Should().Be(Day4));
        (await CollectUnsummarizedAsync(factory)).Should().BeEmpty();
    }

    // ── 6. リースと配線 ────────────────────────────────────────────────────

    // 🔴 [[IADR-0430]] 決定 5 (T-14): **リースを取れない周期は生成しない。**
    // 二重に走ると壊れるのは行ではなく費用である（LLM 呼び出しが倍になる）。
    [Fact]
    public async Task リースを取れない周期は生成しない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();

        var worker = new ClusterSummaryHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new DenyingCoordinator(),
            Options.Create(new ClusterSummaryOptions { Enabled = true }),
            NullLogger<ClusterSummaryHostedService>.Instance);

        (await worker.TryRunCycleAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    // 陽性対照: リースを取れれば走る。
    [Fact]
    public async Task リースを取れた周期は生成する()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();

        var worker = new ClusterSummaryHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new GrantingCoordinator(),
            Options.Create(new ClusterSummaryOptions { Enabled = true }),
            NullLogger<ClusterSummaryHostedService>.Instance);

        (await worker.TryRunCycleAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    // 🔴 [[IADR-0430]] 決定 5: 排他の口は**検出とも健全性とも別値**である。
    // 同値にすると、LLM の応答を待つ長い処理が他の定期処理を丸ごと塞ぐ。
    [Fact]
    public void 排他リースの鍵は検出とも健全性とも別値である()
    {
        PostgresClusterSummaryLeaseCoordinator.AdvisoryLockKey
            .Should().NotBe(PostgresClusterDetectionLeaseCoordinator.AdvisoryLockKey);
        PostgresClusterSummaryLeaseCoordinator.AdvisoryLockKey
            .Should().NotBe(PostgresKnowledgeHealthLeaseCoordinator.AdvisoryLockKey);
    }

    // ADR-0035 決定 3: 周期は**日次**である。
    [Fact]
    public void 生成の周期は日次である()
        => ClusterSummaryHostedService.Interval.Should().Be(TimeSpan.FromDays(1));

    // 🔴 [[IADR-0430]] 決定 6 (T-15): **既定は無効である。** 構成の既定値が `false` であること、
    // そして無効なら**周期が 1 度も回らない**ことを対で固定する（既定値だけを見ても、
    // その値が実際にゲートへ効いているかは分からない）。
    [Fact]
    public async Task 既定ではクラスタ要約の周期が回らない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();

        factory.Services.GetRequiredService<IOptions<ClusterSummaryOptions>>().Value.Enabled
            .Should().BeFalse("既定オフ —— 構成を足すまで LLM の呼び出しは 1 回も起きない");

        var lease = new GrantingCoordinator();
        var disabled = new ClusterSummaryHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            lease,
            Options.Create(new ClusterSummaryOptions()),
            NullLogger<ClusterSummaryHostedService>.Instance);

        (await disabled.TryRunCycleAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        lease.Acquired.Should().Be(0, "無効ならリースも取らない（DB も LLM も触らない）");

        // 陽性対照 —— 有効化すれば回る（「常に false を返す」実装で緑にならない）。
        var enabled = new ClusterSummaryHostedService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            lease,
            Options.Create(new ClusterSummaryOptions { Enabled = true }),
            NullLogger<ClusterSummaryHostedService>.Instance);

        (await enabled.TryRunCycleAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        lease.Acquired.Should().Be(1);
    }

    // 不正な上限は既定へ倒す（起動は落とさない。購読を止めない）。
    [Fact]
    public void 不正な一周期上限は既定へ倒れる()
        => new ClusterSummaryOptions { MaxClustersPerRun = 0 }
            .EffectiveMaxClustersPerRun.Should().Be(ClusterSummaryOptions.DefaultMaxClustersPerRun);

    // ── 器 ─────────────────────────────────────────────────────────────────

    private static Guid[] Ids(int count) => [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid())];

    private static Task SeedAsync(TestWebApplicationFactory factory, Action<GraphDbContext> seed)
        => factory.SeedAsync(db =>
        {
            seed(db);
            return Task.CompletedTask;
        });

    private static void AddDocument(
        GraphDbContext db, Guid id, string title, string confidentiality, bool privateNote = false)
    {
        var attributes = new Dictionary<string, string>
        {
            [ConfidentialityLevels.AttributeKey] = confidentiality,
        };
        if (privateNote)
            attributes[GraphDocumentScope.Key] = GraphDocumentScope.PrivateNote;
        db.Documents.Add(GraphDocument.Create(
            id, title, attributes, bodyHash: null, DateTimeOffset.UnixEpoch));
    }

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

    private static async Task DetectAsync(TestWebApplicationFactory factory, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var job = new ClusterDetectionJob(
            scope.ServiceProvider.GetRequiredService<GraphDbContext>(),
            new FixedClock(now),
            NullLogger<ClusterDetectionJob>.Instance);
        await job.RunAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ClusterSummaryResult> SummarizeAsync(
        TestWebApplicationFactory factory, DateTimeOffset now, StubLlm llm, int maxClustersPerRun = 20)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var job = new ClusterSummaryJob(
            scope.ServiceProvider.GetRequiredService<GraphDbContext>(),
            llm,
            Options.Create(new ClusterSummaryOptions
            {
                Enabled = true,
                MaxClustersPerRun = maxClustersPerRun,
            }),
            new FixedClock(now),
            NullLogger<ClusterSummaryJob>.Instance);
        return await job.RunAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<Guid>> ClusterIdsAsync(TestWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GraphDbContext>()
            .Clusters.AsNoTracking().Select(c => c.ClusterId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<GraphClusterSummary>> SummaryStampsAsync(
        TestWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GraphDbContext>()
            .ClusterSummaries.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<GraphClusterSummaryBody>> SummaryBodiesAsync(
        TestWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GraphDbContext>()
            .ClusterSummaryBodies.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectUnsummarizedAsync(
        TestWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var collector = scope.ServiceProvider.GetRequiredService<KnowledgeHealthCollector>();
        return await collector.CollectUnsummarizedClustersAsync(TestContext.Current.CancellationToken);
    }

    // 記録する LLM スタブ。**封しか受け取れない**ので、封を通っていない値がここへ届く経路は型として無い。
    private sealed class StubLlm(Func<ClusterSummaryPrompt, string?>? respond = null)
        : IClusterSummaryLlmClient
    {
        public List<ClusterSummaryPrompt> Prompts { get; } = [];

        public Task<string?> SummarizeAsync(ClusterSummaryPrompt prompt, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(respond is null
                ? $"{prompt.Members.Count} 件の文書の要約"
                : respond(prompt));
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DenyingCoordinator : IClusterSummaryLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class GrantingCoordinator : IClusterSummaryLeaseCoordinator
    {
        public int Acquired { get; private set; }

        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
        {
            Acquired++;
            return Task.FromResult<IAsyncDisposable?>(new Lease());
        }

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
