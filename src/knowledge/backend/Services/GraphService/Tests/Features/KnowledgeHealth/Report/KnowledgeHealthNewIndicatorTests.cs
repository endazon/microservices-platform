using AwesomeAssertions;
using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Features.KnowledgeHealth.Report;
using GraphService.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace GraphService.Tests.Features.KnowledgeHealth.Report;

// FR-10, FR-17, FR-19, UC-05, SC-10, ADR-0006, ADR-0033 決定 9, ADR-0054,
// [[IADR-0389]] (#1246): #443 の受け口が待っていた**生産者が居なかった 2 指標**。
//
// 本クラスが固定するのは次の 4 点である。
//
//  1. `unresolved-links` の判定（不在・曖昧の**両方**を数え、軸で分ける）
//  2. 🔴 **相手の改名・削除で他文書のリンクが壊れたら、その文書を触らなくても数に現れる**
//     （[[IADR-0389]] 決定 3 の核。解決の失敗を保存する実装ではここが必ず落ちる）
//  3. `edge-type-usage` の内訳（軸＝辺の型名）と、**両端点のどちらかが個人資料なら private-note**
//  4. 陽性対照（解決できるリンクは数えない／組織文書だけの辺は巻き添えで落ちない）
[Trait("TestKind", "Integration")]
public sealed class KnowledgeHealthNewIndicatorTests
{
    // ── 1. unresolved-links: 判定と軸 ──────────────────────────────

    // 🔴 **陽性対照と陰性を対で置く。** 「解決できないリンクだけを数える」は、
    // 「何も数えない」実装でも陰性側だけなら緑になる。
    [Fact]
    public async Task 解決できるリンクは数えず解決できないリンクだけを数える()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, source, "書いた側");
            AddDocument(db, target, "実在する相手");
            AddLinkTarget(db, source, "実在する相手");   // 解決できる
            AddLinkTarget(db, source, "実在しない相手"); // 解決できない
        });

        var observed = await CollectUnresolvedAsync(factory);

        observed.Should().ContainSingle("解決できないリンクだけが観測値になる");
        observed[0].Dimension.Should().Be("not-found");
    }

    // 曖昧一致（同名文書が複数）も**未解決に数える**（[[IADR-0389]] 決定 2）。
    // どちらも辺が作られず、利用者から見れば同じ「繋がっていないリンク」である。
    [Fact]
    public async Task 同名の文書が複数あるリンクは曖昧として数える()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var source = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, source, "書いた側");
            AddDocument(db, Guid.NewGuid(), "重複した題名");
            AddDocument(db, Guid.NewGuid(), "重複した題名");
            AddLinkTarget(db, source, "重複した題名");
        });

        var observed = await CollectUnresolvedAsync(factory);

        observed.Should().ContainSingle();
        observed[0].Dimension.Should().Be("ambiguous",
            "不在と曖昧は運用の直し方が違う（作る vs 改名して一意にする）");
    }

    // ── 2. 🔴 決定 3 の核 ────────────────────────────────────────

    // **相手が改名されると、書いた側を触らなくても未解決になる。**
    //
    // 🔴 **これが「解決の失敗を保存しない」ことの唯一の存在理由である。**
    // 取り込み時の判定を保存する実装では、書いた側の文書が再取り込みされるまで
    // このリンク切れは数に現れない —— リンク切れを数える指標が主因を取りこぼす。
    [Fact]
    public async Task 相手の改名で壊れたリンクは書いた側を触らなくても未解決になる()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, source, "書いた側");
            AddDocument(db, target, "古い題名");
            AddLinkTarget(db, source, "古い題名");
        });

        // 陽性対照: 改名前は 0 件である（これが 0 でないなら以降の 1 件は何の証拠にもならない）。
        (await CollectUnresolvedAsync(factory)).Should().BeEmpty("改名前は解決できている");

        // 相手だけを改名する。**書いた側の document_link_targets は 1 行も触らない。**
        await SeedAsync(factory, db =>
        {
            var doc = db.Documents.Single(d => d.DocumentId == target);
            db.Documents.Remove(doc);
            AddDocument(db, target, "新しい題名");
        });

        var observed = await CollectUnresolvedAsync(factory);

        observed.Should().ContainSingle(
            "相手の改名でリンクは壊れた。書いた側の再取り込みを待たずに数えられること");
        observed[0].Dimension.Should().Be("not-found");
    }

    // 相手の削除でも同じ（削除は「他文書のリンクを壊す」側の操作である）。
    [Fact]
    public async Task 相手の削除で壊れたリンクも書いた側を触らずに未解決になる()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, source, "書いた側");
            AddDocument(db, target, "消える相手");
            AddLinkTarget(db, source, "消える相手");
        });
        (await CollectUnresolvedAsync(factory)).Should().BeEmpty("陽性対照: 削除前は解決できている");

        await SeedAsync(factory, db =>
            db.Documents.Remove(db.Documents.Single(d => d.DocumentId == target)));

        (await CollectUnresolvedAsync(factory)).Should().ContainSingle();
    }

    // ── 3. unresolved-links の文書スコープ ────────────────────────

    // 🔴 **リンクを書いている側のスコープで判定する。** 相手は解決できていない（＝どの文書か
    // 分からない）ため、相手のスコープは原理的に引けない。
    [Fact]
    public async Task 個人資料が書いたリンクの失敗には個人資料のスコープを添える()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var priv = Guid.NewGuid();
        var org = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, priv, "個人メモ", new() { ["doc_scope"] = "private-note" });
            AddDocument(db, org, "組織文書");
            AddLinkTarget(db, priv, "存在しない A");
            AddLinkTarget(db, org, "存在しない B");
        });

        var observed = await CollectUnresolvedAsync(factory);

        observed.Should().HaveCount(2, "生産者は落とさない（除外は受け口が行う）");
        observed.Count(o => o.DocScope == "private-note").Should().Be(1);
        // 陽性対照: 🔴 **属性を持たない文書が巻き添えで個人資料にならない。**
        // 否定（「organization でない」）で書くと属性の無い大多数が落ち、指標が一斉に 0 になる。
        observed.Count(o => o.DocScope is null).Should().Be(1);
    }

    // 観測値の鍵は**リンク先の名前をそのまま含まない**（別サービスの DB へ題名を越境させない）。
    [Fact]
    public async Task 観測値の鍵にリンク先の名前をそのまま含めない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var source = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, source, "書いた側");
            AddLinkTarget(db, source, "極秘の企画書");
        });

        var observed = await CollectUnresolvedAsync(factory);

        observed.Should().ContainSingle();
        observed[0].SubjectKey.Should().NotContain("極秘の企画書");
        observed[0].SubjectKey.Should().StartWith(source.ToString("N"),
            "同じ (文書, リンク先) が同じ鍵になれば重複排除の目的は満たされる");
    }

    // ── 4. edge-type-usage ───────────────────────────────────────

    // 軸に型名が載り、**型ごとに数えられる**（「指標 1 つ＝件数 1 つ」ではない）。
    [Fact]
    public async Task 辺の型ごとの内訳が軸に載る()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        string firstType = string.Empty;
        string secondType = string.Empty;
        await SeedAsync(factory, db =>
        {
            AddDocument(db, a, "A");
            AddDocument(db, b, "B");
            AddDocument(db, c, "C");
            var types = db.EdgeTypes.OrderBy(t => t.Name).Take(2).ToList();
            firstType = types[0].Name;
            secondType = types[1].Name;
            AddEdge(db, a, b, types[0]);
            AddEdge(db, a, c, types[0]);
            AddEdge(db, b, c, types[1]);
        });

        var observed = await CollectEdgeTypeUsageAsync(factory);

        observed.Should().HaveCount(3, "観測値 1 件 ＝ 辺 1 本");
        observed.Count(o => o.Dimension == firstType).Should().Be(2);
        observed.Count(o => o.Dimension == secondType).Should().Be(1);
    }

    // 🔴 **端点のどちらかが個人資料なら private-note を添える**（[[IADR-0389]] 決定 4）。
    // 片側だけを見ると、個人資料から組織文書へ張った辺が組織の指標へ混ざる。
    [Theory]
    [InlineData(true, false)]  // 起点だけが個人資料
    [InlineData(false, true)]  // 相手だけが個人資料
    [InlineData(true, true)]   // 両方
    public async Task 端点のどちらかが個人資料なら辺にも個人資料のスコープを添える(
        bool sourceIsPrivate, bool targetIsPrivate)
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, source, "起点",
                sourceIsPrivate ? new Dictionary<string, string> { ["doc_scope"] = "private-note" } : null);
            AddDocument(db, target, "相手",
                targetIsPrivate ? new Dictionary<string, string> { ["doc_scope"] = "private-note" } : null);
            AddEdge(db, source, target);
        });

        var observed = await CollectEdgeTypeUsageAsync(factory);

        observed.Should().ContainSingle();
        observed[0].DocScope.Should().Be("private-note");
    }

    // 陽性対照。**組織文書だけの辺は巻き添えで落ちない。**
    // これが落ちると、上の 3 ケースは「常に private-note を返す」実装でも緑になる。
    [Fact]
    public async Task 両端点が組織文書なら辺にスコープを添えない()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedAsync(factory, db =>
        {
            AddDocument(db, source, "起点");
            AddDocument(db, target, "相手");
            AddEdge(db, source, target);
        });

        var observed = await CollectEdgeTypeUsageAsync(factory);

        observed.Should().ContainSingle();
        observed[0].DocScope.Should().BeNull();
    }

    // 境界: 辺が 1 本も無ければ観測値は 0 件（**送らない、ではない**。送出は RunAsync が必ず行う）。
    [Fact]
    public async Task 辺が1本も無ければ観測値は0件である()
    {
        using var factory = new TestWebApplicationFactory();
        using var _ = factory.CreateClient();
        await SeedAsync(factory, db => AddDocument(db, Guid.NewGuid(), "孤立"));

        (await CollectEdgeTypeUsageAsync(factory)).Should().BeEmpty();
    }

    // ── 器 ────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectUnresolvedAsync(
        TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<KnowledgeHealthCollector>()
            .CollectUnresolvedLinksAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<KnowledgeHealthObservation>> CollectEdgeTypeUsageAsync(
        TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<KnowledgeHealthCollector>()
            .CollectEdgeTypeUsageAsync(TestContext.Current.CancellationToken);
    }

    private static Task SeedAsync(TestWebApplicationFactory factory, Action<GraphDbContext> seed)
        => factory.SeedAsync(db => { seed(db); return Task.CompletedTask; });

    private static void AddDocument(
        GraphDbContext db, Guid id, string title, Dictionary<string, string>? attributes = null)
        => db.Documents.Add(GraphDocument.Create(
            id, title, attributes ?? [], bodyHash: null, DateTimeOffset.UnixEpoch));

    private static void AddLinkTarget(GraphDbContext db, Guid source, string target)
        => db.DocumentLinkTargets.Add(
            DocumentLinkTarget.Create(source, target, DateTimeOffset.UnixEpoch));

    // 辺の型は起動時の seed（EdgeTypeSeed）で既に入っている。**新しく足さない**。
    private static void AddEdge(GraphDbContext db, Guid source, Guid target, EdgeType? type = null)
    {
        type ??= db.EdgeTypes.First();
        db.Edges.Add(Edge.Create(source, target, type.Id, type.IsSymmetric, EdgeProvenance.Auto));
    }
}
