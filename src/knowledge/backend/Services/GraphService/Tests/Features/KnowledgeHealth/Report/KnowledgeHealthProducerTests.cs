using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Features.KnowledgeHealth.Report;
using GraphService.Infrastructure.ExternalServices;
using GraphService.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GraphService.Tests.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-19, UC-05, SC-10, ADR-0002, ADR-0006, ADR-0033, ADR-0054,
// IADR-0265, [[IADR-0299]] (#443): ナレッジ健全性の観測値の**生産者**。
//
// 受け口（DashboardService）は #443 で実装済みだったが、**本番コードから送っている経路が
// 1 本も無かった**（呼んでいたのはテストだけ）。本クラスが固定するのは次の 5 点である。
//
//  1. 孤立の判定（**両端点**を見る。片側だけを見ない）
//  2. 個人資料の扱い（**スコープを添えて送る**。生産者は落とさない）
//  3. 陽性対照（**スコープを持たない文書が巻き添えで落ちない**）
//  4. 単一書き手化（リースを取れない周期は**収集も送出もしない**）
//  5. 送出の面（パス・本文・**0 件でも送る**・fail-open）
[Trait("TestKind", "Integration")]
public sealed class KnowledgeHealthProducerTests
{
    // ── 1. 孤立の判定 ──────────────────────────────────────────────────────

    // FR-17 (T-33): 辺を 1 本も持たない文書だけが孤立である。
    [Fact]
    public async Task 辺を持たない文書だけが孤立として報告される()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var orphan = Guid.NewGuid();
        var linkedA = Guid.NewGuid();
        var linkedB = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, orphan, "孤立");
            AddDocument(db, linkedA, "A");
            AddDocument(db, linkedB, "B");
            AddEdge(db, linkedA, linkedB);
        });

        var observed = await CollectAsync(factory);

        observed.Select(o => o.SubjectKey).Should().BeEquivalentTo([orphan.ToString()],
            "辺の端点に現れる文書は孤立ではない");
    }

    // FR-17 (T-34): 🔴 **両端点を見る。** 対称型は書き込み時に (min, max) へ正規化されるため
    // （IADR-0242 決定 9）、Source だけを見ると「参照されている側」を孤立と数える。
    // 計画の定義「どの文書からも参照されず、どの文書も参照していない」の字義でもある。
    [Fact]
    public async Task 被参照だけの文書は孤立ではない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, source, "参照元");
            AddDocument(db, target, "参照先");
            AddEdge(db, source, target);
        });

        var observed = await CollectAsync(factory);

        observed.Should().BeEmpty("Target 側にしか現れない文書も「参照されている」");
    }

    // FR-17 (T-35): 辺そのものが 1 本も無ければ、全文書が孤立である（境界）。
    [Fact]
    public async Task 辺が1本も無ければ全文書が孤立である()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, Guid.NewGuid(), "1");
            AddDocument(db, Guid.NewGuid(), "2");
        });

        (await CollectAsync(factory)).Should().HaveCount(2);
    }

    // ── 2・3. 個人資料の扱いと陽性対照 ──────────────────────────────────────

    // FR-19, SC-10 (T-36): 🔴 **個人資料は生産者側で落とさず、スコープを添えて送る。**
    // 除外を強制するのは受け手である（件数だけを渡すと、除外したかを受け手が確かめられない）。
    // **スコープを添えないと受け手は個人資料を組織文書として数える。**
    [Fact]
    public async Task 個人資料の孤立には文書スコープが添えられる()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var note = Guid.NewGuid();
        await SeedAsync(factory, db => AddDocument(db, note, "個人メモ",
            new Dictionary<string, string> { [GraphDocumentScope.Key] = GraphDocumentScope.PrivateNote }));

        var observed = await CollectAsync(factory);

        observed.Should().ContainSingle()
            .Which.DocScope.Should().Be(GraphDocumentScope.PrivateNote,
                "受け手はこの値でしか個人資料を落とせない");
    }

    // FR-19, SC-10 (T-37): 🔴 **陽性対照。** 判定を「organization でない」と否定形で書くと、
    // `doc_scope` を持たない文書（実データの大半。ADR-0054 は既存文書へ遡及付与しない）が
    // すべて個人資料として送られ、受け手が全部落として**孤立文書数が一斉に 0 になる**。
    // この対照が無いと、集合帰属版と否定版は動作で見分けがつかない。
    [Theory]
    [InlineData(null)]           // 属性そのものが無い
    [InlineData("organization")] // 明示的に組織文書
    public async Task スコープを持たないか組織文書の孤立はスコープ無しで送られる(string? scope)
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var attributes = scope is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [GraphDocumentScope.Key] = scope };
        await SeedAsync(factory, db => AddDocument(db, Guid.NewGuid(), "組織文書", attributes));

        var observed = await CollectAsync(factory);

        observed.Should().ContainSingle()
            .Which.DocScope.Should().BeNull("個人資料でない文書は受け手の除外対象にしない");
    }

    // FR-19 (T-38): 綴りの大小は個人資料の判定を変えない（受け口側と同じ規律）。
    [Fact]
    public async Task 個人資料の綴りの大小は判定を変えない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        await SeedAsync(factory, db => AddDocument(db, Guid.NewGuid(), "メモ",
            new Dictionary<string, string> { [GraphDocumentScope.Key] = "PRIVATE-NOTE" }));

        (await CollectAsync(factory)).Should().ContainSingle()
            .Which.DocScope.Should().Be(GraphDocumentScope.PrivateNote);
    }

    // ── 4. 単一書き手化（排他リース） ───────────────────────────────────────

    // FR-10, [[IADR-0299]] 決定 3 (T-39): 🔴 **リースを取得できない周期は収集も送出もしない。**
    // 受け口は全量スナップショット置換であり、2 レプリカが同時に走ると片方の DELETE が
    // 他方の INSERT 済み行を消して**恒久的に過少な件数**が残る（自然回復しない）。
    [Fact]
    public async Task リースを取得できない周期は報告しない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        await SeedAsync(factory, db => AddDocument(db, Guid.NewGuid(), "孤立"));
        var reporter = new RecordingReporter();
        var worker = BuildWorker(factory, new DenyingCoordinator(), reporter);

        var ran = await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        ran.Should().BeFalse();
        reporter.Calls.Should().BeEmpty("スキップした周期は 1 通も送らない（fail-safe）");
    }

    // FR-10, [[IADR-0299]] 決定 3 (T-40): **陽性対照。** リースを取得できた周期は報告し、解放する。
    // これが無いと、上のテストは「そもそも一度も報告しない実装」でも緑になる。
    [Fact]
    public async Task リースを取得できた周期は報告しリースを解放する()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        await SeedAsync(factory, db => AddDocument(db, Guid.NewGuid(), "孤立"));
        var reporter = new RecordingReporter();
        var lease = new RecordingLease();
        var worker = BuildWorker(factory, new GrantingCoordinator(lease), reporter);

        var ran = await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        ran.Should().BeTrue();
        // ★［#1186 → #1246］1 周期で **4 指標**を報告する。
        // 🔴 **ここが「生産者が居る」の唯一の証拠である。** 収集の実装があっても
        // RunAsync が呼んでいなければ指標は永久に 0 のままであり、#1246 が名指しした
        // 「受け口はあるのに生産者が居ない」状態と区別がつかない。
        reporter.Calls.Select(c => c.Indicator).Should().BeEquivalentTo([
            KnowledgeHealthIndicators.OrphanDocuments,
            KnowledgeHealthIndicators.StaleDocuments,
            KnowledgeHealthIndicators.UnresolvedLinks,
            KnowledgeHealthIndicators.EdgeTypeUsage,
        ]);
        lease.Disposed.Should().BeTrue("報告後にリースを解放する（次周期で他レプリカも取得できる）");
    }

    // ── 5. 送出の面 ────────────────────────────────────────────────────────

    // FR-10 (T-41): 🔴 **孤立が 0 件でも報告を送る。**
    // 受け口はスナップショット置換であり、送らないと**前回の件数が恒久的に残る**
    // （孤立を解消したのに数字が減らない）。「無駄な送信の抑止」と読み替えてはならない。
    [Fact]
    public async Task 孤立が0件でも報告を送る()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var reporter = new RecordingReporter();
        var worker = BuildWorker(factory, new GrantingCoordinator(new RecordingLease()), reporter);

        await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        reporter.Call(KnowledgeHealthIndicators.OrphanDocuments)
            .Observations.Should().BeEmpty("空のスナップショットで受け口の既存行を落とす");
    }

    // FR-10 (T-42): アダプタが実際に投げるパスと本文を固定する。
    // **型ではなく綴りが噛み合っている必要がある** —— 送るのは匿名オブジェクトであり、
    // `docScope` を `docscope` と書いても C# は何も言わない。
    [Fact]
    public async Task 送出のパスと本文は指標名と観測値だけで構成される()
    {
        var handler = new FakeIngressHandler();
        var reporter = new HttpKnowledgeHealthReporter(
            new SingleClientHttpClientFactory(handler),
            NewReportMetrics(),
            NullLogger<HttpKnowledgeHealthReporter>.Instance);

        await reporter.ReportAsync(KnowledgeHealthIndicators.OrphanDocuments,
            [new KnowledgeHealthObservation("doc-1", GraphDocumentScope.PrivateNote)],
            ct: TestContext.Current.CancellationToken);

        handler.LastPath.Should().Be(HttpKnowledgeHealthReporter.ObservationsPath,
            "★ 受け口 ReportKnowledgeHealthEndpoint.ObservationsPath と 1 バイトでも違えば観測値は届かない");

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["indicator", "observations"], "2 項目ちょうど");
        var first = body.RootElement.GetProperty("observations")[0];
        first.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["subjectKey", "docScope"]);
        first.GetProperty("docScope").GetString().Should().Be(GraphDocumentScope.PrivateNote);
    }

    // FR-10 (T-43): 受け口が落ちていても例外を投げない（fail-open）。
    // 本サービスは DocumentUpdated / DocumentDeleted の購読ホストでもあり、
    // **指標の送出失敗で購読を止めない**。
    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("invalid-op")]
    [InlineData("status-500")]
    [InlineData("status-404")]
    public async Task 受け口が落ちていても報告は例外を投げない(string mode)
    {
        var handler = new FakeIngressHandler
        {
            Response = mode switch
            {
                "http" => () => throw new HttpRequestException("接続できない"),
                "timeout" => () => throw new TaskCanceledException("タイムアウト"),
                "invalid-op" => () => throw new InvalidOperationException("BaseAddress 不整合"),
                "status-500" => () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                _ => () => new HttpResponseMessage(HttpStatusCode.NotFound),
            },
        };
        var reporter = new HttpKnowledgeHealthReporter(
            new SingleClientHttpClientFactory(handler),
            NewReportMetrics(),
            NullLogger<HttpKnowledgeHealthReporter>.Instance);

        var act = async () => await reporter.ReportAsync(
            KnowledgeHealthIndicators.OrphanDocuments, [], ct: CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // FR-10 (T-44): **呼び出し元のキャンセルだけは伝播させる。**
    // 握ると「シャットダウンされたのに続行した」ように見える。
    [Fact]
    public async Task 呼び出し元のキャンセルは握り潰さない()
    {
        var handler = new FakeIngressHandler
        {
            Response = () => throw new TaskCanceledException("キャンセル"),
        };
        var reporter = new HttpKnowledgeHealthReporter(
            new SingleClientHttpClientFactory(handler),
            NewReportMetrics(),
            NullLogger<HttpKnowledgeHealthReporter>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await reporter.ReportAsync(
            KnowledgeHealthIndicators.OrphanDocuments, [], ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── 6. 陳腐化文書数（stale-documents） ─────────────────────
    //
    // FR-10, UC-05, SC-10, planning#494, [[IADR-0353]] (#1186)。
    // 🔴 **本節の中心は T-48 である** —— タグを付け替えただけの文書が件数から消えないこと。
    // 計画の言い方では「指標が自分の改善作業で消えるなら、それは測定ではない」。

    // FR-10 (T-45): しきい値より古い本文更新は陳腐である（境界の**外側**）。
    [Fact]
    public async Task 本文が181日前に更新された文書は陳腐化として報告される()
    {
        var observed = await CollectStaleAsync(Now, doc => doc.WithBodyAgeInDays(181));

        observed.Should().ContainSingle("180 日を超えた本文更新は陳腐である");
    }

    // FR-10 (T-46): **境界を両側から測る。** しきい値の内側は陳腐ではない。
    [Fact]
    public async Task 本文が179日前に更新された文書は陳腐化に含まれない()
    {
        var observed = await CollectStaleAsync(Now, doc => doc.WithBodyAgeInDays(179));

        observed.Should().BeEmpty();
    }

    // FR-10 (T-47): しきい値**ちょうど**は陳腐でない（`<` であって `<=` ではない）。
    [Fact]
    public async Task しきい値ちょうどの文書は陳腐化に含まれない()
    {
        var observed = await CollectStaleAsync(Now, doc => doc.WithBodyAgeInDays(180));

        observed.Should().BeEmpty("境界は開いている。ちょうどの日を含めると 1 日早く警告が出る");
    }

    // 🔴 FR-10 (T-48): **本裁定の中心（受け入れ基準 3）。**
    // 200 日前に本文が更新され、**昨日タグを付け替えただけ**の文書は**依然として陳腐**である。
    // ここを落とすと、棚卸し作業そのもの（タグ整理）が指標を改善させる。
    // 判定は `TryApply` を通した**振る舞い**で測る（列へ直接書かない —— 直接書くと、
    // 前進規則が壊れていても緑になる）。
    [Fact]
    public async Task 昨日タグを付け替えただけの古い文書は依然として陳腐化に含まれる()
    {
        var observed = await CollectStaleAsync(Now, doc =>
        {
            doc.WithBodyAgeInDays(200);
            // メタデータのみの更新: **同じ指紋**・新しい属性・新しい更新時刻。
            doc.Node.TryApply("t",
                new Dictionary<string, string> { ["dept"] = "sales" }, doc.Hash, Now.AddDays(-1));
        });

        observed.Should().ContainSingle(
            "🔴 タグ整理で件数が減る指標は測定ではない（planning#494 決定 2）");
    }

    // FR-10 (T-49): **陽性対照（受け入れ基準 4）。** 昨日**本文を**編集した文書は陳腐でない。
    // T-48 と対で置く —— これが無いと「常に陳腐と数える実装」でも T-48 は緑になる。
    [Fact]
    public async Task 昨日本文を編集した古い文書は陳腐化に含まれない()
    {
        var observed = await CollectStaleAsync(Now, doc =>
        {
            doc.WithBodyAgeInDays(200);
            // 本文の編集: **指紋が変わる**。
            doc.Node.TryApply("t", [], "hash-new", Now.AddDays(-1));
        });

        observed.Should().BeEmpty();
    }

    // FR-10 (T-50): 個人資料には文書スコープが添う（受け入れ基準 8）。
    // **陽性対照つき** —— スコープを持たない文書は巻き添えで落とされない。
    [Theory]
    [InlineData(null, null)]
    [InlineData("organization", null)]
    [InlineData(GraphDocumentScope.PrivateNote, GraphDocumentScope.PrivateNote)]
    [InlineData("PRIVATE-NOTE", GraphDocumentScope.PrivateNote)]
    public async Task 陳腐化の観測値には文書スコープが添えられる(string? scope, string? expected)
    {
        var attributes = scope is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [GraphDocumentScope.Key] = scope };

        var observed = await CollectStaleAsync(Now, doc =>
        {
            doc.WithBodyAgeInDays(200);
            doc.Node.TryApply("t", attributes, doc.Hash, Now.AddDays(-1));
        });

        observed.Should().ContainSingle().Which.DocScope.Should().Be(expected);
    }

    // FR-10 (T-51): **0 件でも送る**（受け入れ基準 5）。孤立文書と同じ理由
    // —— 受け口はスナップショット置換であり、送らないと前回の件数が残る。
    [Fact]
    public async Task 陳腐化が0件でも報告を送る()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var reporter = new RecordingReporter();
        var worker = BuildWorker(factory, new GrantingCoordinator(new RecordingLease()), reporter);

        await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        var call = reporter.Call(KnowledgeHealthIndicators.StaleDocuments);
        call.Observations.Should().BeEmpty();
        call.ThresholdDays.Should().Be(KnowledgeHealthOptions.DefaultStaleDocumentThresholdDays,
            "🔴 0 件のときこそしきい値が要る（件数だけでは意味が読めない）");
    }

    // FR-10 (T-52): 構成なしなら **180 日**で判定する（受け入れ基準 7）。
    [Fact]
    public async Task 構成が無ければ180日で判定する()
    {
        var included = await CollectStaleAsync(Now, doc => doc.WithBodyAgeInDays(181));
        var excluded = await CollectStaleAsync(Now, doc => doc.WithBodyAgeInDays(179));

        included.Should().ContainSingle();
        excluded.Should().BeEmpty();
    }

    // FR-10 (T-53): 構成で **90 日**にすると、判定も報告に添えるしきい値も 90 になる
    // （受け入れ基準 6。planning#494 決定 3「配備時の構成で変更できる」）。
    [Fact]
    public async Task 構成でしきい値を変えると判定も報告値も追随する()
    {
        var options = new KnowledgeHealthOptions { StaleDocumentThresholdDays = 90 };

        var observed = await CollectStaleAsync(Now, doc => doc.WithBodyAgeInDays(100), options);

        observed.Should().ContainSingle("90 日を超えているので、既定の 180 日では拾われない文書が陳腐になる");

        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var reporter = new RecordingReporter();
        var worker = BuildWorker(
            factory, new GrantingCoordinator(new RecordingLease()), reporter, options);

        await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        reporter.Call(KnowledgeHealthIndicators.StaleDocuments).ThresholdDays.Should().Be(90);
    }

    // FR-10, [[IADR-0353]] 決定 3 (T-54): 🔴 **不正な構成では既定へ倒す。起動は落とさない。**
    // 落とすと本サービスの DocumentUpdated / DocumentDeleted 購読ごと止まる（指標の都合で
    // 購読を止めない）。**報告に添える値も倒した後の 180 になる** —— 画面へ嘘の数字を出さない。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task しきい値の構成が不正なら既定へ倒し報告値も既定になる(int configured)
    {
        var options = new KnowledgeHealthOptions { StaleDocumentThresholdDays = configured };
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var reporter = new RecordingReporter();
        var worker = BuildWorker(
            factory, new GrantingCoordinator(new RecordingLease()), reporter, options);

        await worker.TryRunCycleAsync(TestContext.Current.CancellationToken);

        reporter.Call(KnowledgeHealthIndicators.StaleDocuments).ThresholdDays
            .Should().Be(KnowledgeHealthOptions.DefaultStaleDocumentThresholdDays);
    }

    // FR-10 (T-55): 送出の面。**しきい値を持つ指標だけ本文に `thresholdDays` が現れる。**
    // 型ではなく綴りが噛み合っている必要がある（匿名オブジェクトは綴り違いを黙って通す）。
    [Fact]
    public async Task 陳腐化の送出本文にはしきい値が現れる()
    {
        var handler = new FakeIngressHandler();
        var reporter = new HttpKnowledgeHealthReporter(
            new SingleClientHttpClientFactory(handler),
            NewReportMetrics(),
            NullLogger<HttpKnowledgeHealthReporter>.Instance);

        await reporter.ReportAsync(KnowledgeHealthIndicators.StaleDocuments,
            [new KnowledgeHealthObservation("doc-1", null)], 180,
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["indicator", "observations", "thresholdDays"], "3 項目ちょうど");
        body.RootElement.GetProperty("thresholdDays").GetInt32().Should().Be(180);
    }

    // FR-10 (T-56): **陰性対照。** しきい値を持たない指標の本文は 2 項目のままである
    // （受け口は欠落を「しきい値なし」として扱う）。
    [Fact]
    public async Task しきい値を渡さない報告の本文は2項目のままである()
    {
        var handler = new FakeIngressHandler();
        var reporter = new HttpKnowledgeHealthReporter(
            new SingleClientHttpClientFactory(handler),
            NewReportMetrics(),
            NullLogger<HttpKnowledgeHealthReporter>.Instance);

        await reporter.ReportAsync(KnowledgeHealthIndicators.OrphanDocuments, [],
            ct: TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.LastBody!);
        body.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["indicator", "observations"]);
    }

    // ── 器 ─────────────────────────────────────────────────────────────────

    // 陳腐化の収集を 1 回だけ決定的に回す。
    //
    // 🔴 **`BodyUpdatedAt` へ直接書かない。** 種を播くのも更新するのも
    // `GraphDocument.Create` / `TryApply` を通す —— 列へ直接書くと、前進規則が壊れていても
    // テストが緑になる（測っているのが実装ではなく種になる）。
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");

    private sealed class StaleSeed(DateTimeOffset now)
    {
        public GraphDocument Node { get; private set; } = default!;

        public string Hash => "hash-0";

        // 本文の更新が `days` 日前だった文書を作る。
        public void WithBodyAgeInDays(int days)
            => Node = GraphDocument.Create(
                Guid.NewGuid(), "t", [], Hash, now.AddDays(-days));
    }

    private static async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectStaleAsync(
        DateTimeOffset now,
        Action<StaleSeed> arrange,
        KnowledgeHealthOptions? options = null)
    {
        var opts = options ?? new KnowledgeHealthOptions();
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();

        var seed = new StaleSeed(now);
        arrange(seed);
        await SeedAsync(factory, db => db.Documents.Add(seed.Node));

        using var scope = factory.Services.CreateScope();
        var collector = new KnowledgeHealthCollector(
            scope.ServiceProvider.GetRequiredService<GraphDbContext>(),
            new RecordingReporter(),
            Options.Create(opts),
            new FixedClock(now),
            NullLogger<KnowledgeHealthCollector>.Instance);

        return await collector.CollectStaleDocumentsAsync(
            opts.EffectiveStaleDocumentThresholdDays, TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectAsync(
        TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var collector = scope.ServiceProvider.GetRequiredService<KnowledgeHealthCollector>();
        return await collector.CollectOrphanDocumentsAsync(TestContext.Current.CancellationToken);
    }

    // ★［2026-09-05 / #1246］送出アダプタは受理時に計器を打つ（`absent` 系アラートの土台）。
    // 計器そのものの主張は `KnowledgeHealthReportMetricsTests` が持つ。ここでは器だけ渡す。
    private static GraphService.Common.Observability.KnowledgeHealthReportMetrics NewReportMetrics()
        => new(new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());

    private static Task SeedAsync(TestWebApplicationFactory factory, Action<GraphDbContext> seed)
        => factory.SeedAsync(db =>
        {
            seed(db);
            return Task.CompletedTask;
        });

    private static void AddDocument(
        GraphDbContext db, Guid id, string title, Dictionary<string, string>? attributes = null)
        => db.Documents.Add(GraphDocument.Create(
            id, title, attributes ?? [], bodyHash: null, DateTimeOffset.UnixEpoch));

    // 辺の型は起動時の seed（EdgeTypeSeed）で既に入っている。**新しく足さない** ——
    // 一意制約のある名前を重ねると、本題（孤立の判定）と無関係な理由で落ちる。
    private static void AddEdge(GraphDbContext db, Guid source, Guid target)
    {
        var type = db.EdgeTypes.First();
        db.Edges.Add(Edge.Create(
            source, target, type.Id, type.IsSymmetric, EdgeProvenance.Auto));
    }

    private static KnowledgeHealthHostedService BuildWorker(
        TestWebApplicationFactory factory,
        IKnowledgeHealthLeaseCoordinator coordinator,
        IKnowledgeHealthReporter reporter,
        KnowledgeHealthOptions? options = null,
        TimeProvider? clock = null) =>
        new(
            new ReporterOverridingScopeFactory(
                factory.Services.GetRequiredService<IServiceScopeFactory>(), reporter,
                options ?? new KnowledgeHealthOptions(), clock ?? TimeProvider.System),
            coordinator,
            NullLogger<KnowledgeHealthHostedService>.Instance);

    // 収集は実 DbContext で回しつつ、送出・構成・時計だけを差し替える。
    private sealed class ReporterOverridingScopeFactory(
        IServiceScopeFactory inner, IKnowledgeHealthReporter reporter,
        KnowledgeHealthOptions options, TimeProvider clock) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(inner.CreateScope(), reporter, options, clock);

        private sealed class Scope(
            IServiceScope inner, IKnowledgeHealthReporter reporter,
            KnowledgeHealthOptions options, TimeProvider clock)
            : IServiceScope, IAsyncDisposable, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(KnowledgeHealthCollector))
                    return new KnowledgeHealthCollector(
                        inner.ServiceProvider.GetRequiredService<GraphDbContext>(),
                        reporter,
                        Options.Create(options),
                        clock,
                        NullLogger<KnowledgeHealthCollector>.Instance);
                return inner.ServiceProvider.GetService(serviceType);
            }

            public void Dispose() => inner.Dispose();

            public ValueTask DisposeAsync()
            {
                inner.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    // 陳腐化の判定を決定的に測るための時計（GraphDocumentSyncConsumerTests と同じ最小実装）。
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record ReportCall(
        string Indicator, IReadOnlyList<KnowledgeHealthObservation> Observations, int? ThresholdDays);

    private sealed class RecordingReporter : IKnowledgeHealthReporter
    {
        public List<ReportCall> Calls { get; } = [];

        public ReportCall Call(string indicator) =>
            Calls.Single(c => c.Indicator == indicator);

        public Task ReportAsync(string indicator,
            IReadOnlyList<KnowledgeHealthObservation> observations,
            int? thresholdDays = null, CancellationToken ct = default)
        {
            Calls.Add(new ReportCall(indicator, observations, thresholdDays));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLease : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GrantingCoordinator(IAsyncDisposable lease) : IKnowledgeHealthLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(lease);
    }

    private sealed class DenyingCoordinator : IKnowledgeHealthLeaseCoordinator
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class FakeIngressHandler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Response { get; init; }
            = () => new HttpResponseMessage(HttpStatusCode.Accepted);

        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return Response();
        }
    }

    private sealed class SingleClientHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://dashboard-service:8080"),
            Timeout = HttpKnowledgeHealthReporter.SendTimeout,
        };
    }
}
