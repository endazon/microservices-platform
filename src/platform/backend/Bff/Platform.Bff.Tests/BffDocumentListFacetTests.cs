using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace Platform.Bff.Tests;

// FR-06, UC-03, SC-05, NFR-16, [[IADR-0009]], [[IADR-0545]], [[IADR-0561]] (#1923 の残り):
// 文書管理の一覧（`GET /bff/documents`）の**データソース・タブ（機械の収集か否か）・機密区分・タグ・プロジェクト・保存日の範囲**の絞り込みと、
// **データソース別・プロジェクト別の件数**。
//
// 🔴 **絞り込みは ABAC の後に当て、件数はどれも ABAC で絞った後の数である**（IADR-0545 決定 2 と同じ作法）。
//   権限外の文書・他人の個人資料を混ぜた台帳と混ぜない台帳で、新しい絞り込みを付けた応答が 1 ビットも変わらないことで示す（陰性）。
//   陽性対照として、混ぜた文書が絞り込みの条件に一致すること（＝落としているのは ABAC / 個人資料の除外である）を先に確かめる。
public class BffDocumentListFacetTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDocumentListFacetTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        // 読めるのは internal と public（confidential は権限外）。
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal", "public"])];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
        _factory.DocumentReadServerPageSize = null;
        _factory.DocumentReadFailContinuation = false;
        while (_factory.ListDocumentsRequests.TryDequeue(out _)) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static DocumentDto Doc(int n, string confidentiality = "internal", Dictionary<string, string>? attributes = null,
        List<string>? tags = null, string? dataSource = null, int createdDay = 0)
    {
        var attrs = new Dictionary<string, string>(attributes ?? []) { ["confidentiality"] = confidentiality };
        return new DocumentDto
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{n:D12}"),
            Title = $"文書 {n}",
            Status = "published",
            Attributes = attrs,
            Tags = tags ?? [],
            DataSourceName = dataSource,
            CreatedAt = Day0.AddDays(createdDay),
            UpdatedAt = Day0.AddMinutes(100 - n),
        };
    }

    private static Dictionary<string, string> Collected(string source) =>
        new() { ["kind"] = "News", ["source"] = source, ["project"] = "ai-stock-trading" };

    private static readonly Dictionary<string, string> Report =
        new() { ["kind"] = "Daily", ["periodKey"] = "2026-10-01", ["project"] = "ai-stock-trading" };

    // 読める 6 件（1・2・3・5・6・7）と、権限外の 2 件（4・8）。権限外の 2 件はどの絞り込みにも一致し得る形にしてある。
    private static List<DocumentDto> Mixed() =>
    [
        Doc(1, attributes: Collected("kabutan"), tags: ["News", "kabutan"], createdDay: 1),
        Doc(2, attributes: Collected("tdnet"), tags: ["Disclosure"], createdDay: 2),
        Doc(3, attributes: Report, createdDay: 3),
        Doc(4, "confidential", attributes: Collected("kabutan"), tags: ["News", "kabutan"], createdDay: 1),
        Doc(5, "public", tags: ["規程"], createdDay: 4),
        Doc(6, dataSource: "共有フォルダ", tags: ["規程"], createdDay: 5),
        Doc(7, attributes: Collected("kabutan"), createdDay: 6),
        Doc(8, "confidential", attributes: Report, tags: ["規程"], dataSource: "共有フォルダ", createdDay: 4),
    ];

    private static List<DocumentDto> VisibleOnly() => Mixed().Where(d => d.Attributes["confidentiality"] != "confidential").ToList();

    private async Task<DocumentListPageDto> PageAsync(string query)
    {
        var resp = await _factory.CreateClient().GetAsync($"/bff/documents{query}", Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        return (await resp.Content.ReadFromJsonAsync<DocumentListPageDto>(Ct))!;
    }

    private static IEnumerable<int> Numbers(DocumentListPageDto page) => page.Items.Select(d => (int)d.Id.ToByteArray()[15]);

    // ── データソースの鍵（導出の順） ─────────────────────────────

    [Fact]
    public void データソースの鍵はデータソース_収集_報告書_手入力の順で決まる()
    {
        // データソース（コネクタ）の表示名が最優先 —— 属性 `source` を持っていても `ds:` になる。
        DocumentListSources.KeyOf(Doc(1, attributes: Collected("kabutan"), dataSource: "共有フォルダ")).Should().Be("ds:共有フォルダ");
        DocumentListSources.KeyOf(Doc(1, attributes: Collected("kabutan"))).Should().Be("col:kabutan");
        DocumentListSources.KeyOf(Doc(1, attributes: Report)).Should().Be("report");
        // `kind` だけ・`periodKey` だけでは報告書と見なさない。
        DocumentListSources.KeyOf(Doc(1, attributes: new() { ["kind"] = "Daily" })).Should().Be("manual");
        DocumentListSources.KeyOf(Doc(1)).Should().Be("manual");

        DocumentListSources.OriginOf("col:kabutan").Should().Be("collected");
        DocumentListSources.OriginOf("report").Should().Be("curated");
        DocumentListSources.OriginOf("ds:col:x").Should().Be("curated");
    }

    // ── 絞り込み ─────────────────────────────────────────────

    [Theory]
    [InlineData("?origin=collected", new[] { 1, 2, 7 })]
    [InlineData("?origin=curated", new[] { 3, 5, 6 })]
    [InlineData("?source=col%3Akabutan", new[] { 1, 7 })]
    [InlineData("?source=ds%3A%E5%85%B1%E6%9C%89%E3%83%95%E3%82%A9%E3%83%AB%E3%83%80", new[] { 6 })]
    [InlineData("?source=report", new[] { 3 })]
    [InlineData("?source=manual", new[] { 5 })]
    [InlineData("?source=col%3Aunknown", new int[0])]
    [InlineData("?confidentiality=public", new[] { 5 })]
    [InlineData("?confidentiality=internal&origin=curated", new[] { 3, 6 })]
    [InlineData("?tag=%E8%A6%8F%E7%A8%8B", new[] { 5, 6 })]
    [InlineData("?tag=news", new int[0])]
    [InlineData("?project=ai-stock-trading", new[] { 1, 2, 3, 7 })]
    [InlineData("?project=(none)", new[] { 5, 6 })]
    [InlineData("?createdFrom=2026-10-03T00:00:00Z", new[] { 2, 3, 5, 6, 7 })]
    [InlineData("?createdBefore=2026-10-03T00:00:00Z", new[] { 1 })]
    // オフセットを読む: 10-02 08:00（+09:00）は 10-01 23:00Z で、10-02 00:00Z に作られた 1 を含む（オフセットを捨てると落ちる）。
    [InlineData("?createdFrom=2026-10-02T08:00:00%2B09:00&createdBefore=2026-10-05T00:00:00Z", new[] { 1, 2, 3 })]
    public async Task 絞り込みは窓と総件数を絞り全体の件数は絞る前の数(string query, int[] expected)
    {
        _factory.StubDocumentList = Mixed();

        var page = await PageAsync(query);

        Numbers(page).Should().Equal(expected);
        page.Total.Should().Be(expected.Length);
        page.UnfilteredTotal.Should().Be(6);
    }

    // データソース別・プロジェクト別の件数は絞り込みに依らない（集合は `unfilteredTotal` と同じ）。
    [Fact]
    public async Task データソース別とプロジェクト別の件数は絞り込みに依らない()
    {
        _factory.StubDocumentList = Mixed();

        var page = await PageAsync("?origin=collected&tag=News");

        Numbers(page).Should().Equal(1);
        page.SourceCounts.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["col:kabutan"] = 2,
            ["col:tdnet"] = 1,
            ["report"] = 1,
            ["manual"] = 1,
            ["ds:共有フォルダ"] = 1,
        });
        page.ProjectCounts.Should().BeEquivalentTo(new Dictionary<string, int> { ["ai-stock-trading"] = 4, ["(none)"] = 2 });
        page.SourceCounts.Values.Sum().Should().Be(page.UnfilteredTotal);
        page.ProjectCounts.Values.Sum().Should().Be(page.UnfilteredTotal);
    }

    // 後方互換: 新しいクエリを付けない要求は全件（ABAC 後）を返し、件数の項目が増えるだけ。
    [Fact]
    public async Task 新しいクエリを付けない要求は従前どおり全件を返す()
    {
        _factory.StubDocumentList = Mixed();

        var page = await PageAsync("?offset=0&limit=50");

        Numbers(page).Should().Equal(1, 2, 3, 5, 6, 7);
        (page.Total, page.UnfilteredTotal).Should().Be((6, 6));
    }

    // データソースの表示名と原本の所在は gRPC を往復して一覧の応答に載る（画面の列・出典のリンクの材料）。
    [Fact]
    public async Task データソースの表示名と原本の所在が一覧の応答に載る()
    {
        _factory.StubDocumentList = [Doc(6, dataSource: "共有フォルダ") with { OriginalUri = "https://example.com/a" }, Doc(5)];

        var page = await PageAsync("");

        page.Items[0].DataSourceName.Should().Be("共有フォルダ");
        page.Items[0].OriginalUri.Should().Be("https://example.com/a");
        page.Items[1].DataSourceName.Should().BeNull();
        page.Items[1].OriginalUri.Should().BeNull();
    }

    // 🔴 件数に出した鍵は必ず選べる: 長い出典名（属性 `source` に上限は無い）の鍵は受理側の上限で切って数え、
    // 切った鍵で絞ると 400 にならずにその文書に当たる。サロゲートペアは割らない。
    [Fact]
    public async Task 長い出典名の鍵は受理の上限で切って数え選べる()
    {
        var longSource = new string('s', 500);
        var surrogate = new string('a', DocumentListFilter.MaxSourceLength - 5) + "😀😀"; // "col:" と合わせて上限の 1 つ手前で上位サロゲートが来る
        _factory.StubDocumentList = [Doc(1, attributes: Collected(longSource)), Doc(2, attributes: Collected(surrogate)), Doc(5)];

        var page = await PageAsync("");
        var keys = page.SourceCounts.Keys.Where(k => k.StartsWith("col:", StringComparison.Ordinal)).ToList();
        keys.Should().HaveCount(2).And.OnlyContain(k => k.Length <= DocumentListFilter.MaxSourceLength);
        keys.Should().OnlyContain(k => !char.IsHighSurrogate(k[k.Length - 1]));

        foreach (var (key, n) in new[] { (keys.Single(k => k.Contains('s')), 1), (keys.Single(k => k.Contains('a')), 2) })
        {
            var filtered = await PageAsync($"?source={Uri.EscapeDataString(key)}");
            Numbers(filtered).Should().Equal(n);
        }
    }

    // ── 🔴 存在秘匿（IADR-0009） ──────────────────────────────────

    [Theory]
    [InlineData("?origin=collected")]
    [InlineData("?origin=curated")]
    [InlineData("?source=col%3Akabutan")]
    [InlineData("?source=report")]
    [InlineData("?source=ds%3A%E5%85%B1%E6%9C%89%E3%83%95%E3%82%A9%E3%83%AB%E3%83%80")]
    [InlineData("?tag=%E8%A6%8F%E7%A8%8B")]
    [InlineData("?tag=News&project=ai-stock-trading")]
    [InlineData("?createdFrom=2026-10-01T00:00:00Z&createdBefore=2026-10-05T00:00:00Z")]
    [InlineData("?origin=collected&limit=1&offset=1")]
    [InlineData("")]
    public async Task 権限外の文書は新しい絞り込みの件数にもデータソース別の件数にも現れない(string query)
    {
        // 陽性対照: 区分を広げたスコープでは、権限外の 2 件も数えられ、データソース別の件数にも入る（落としているのは ABAC である）。
        _factory.StubDocumentList = Mixed();
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal", "public", "confidential"])];
        var control = await PageAsync("?source=col%3Akabutan");
        (control.Total, control.SourceCounts["col:kabutan"], control.SourceCounts["ds:共有フォルダ"]).Should().Be((3, 3, 2));
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal", "public"])];

        var withHidden = await PageAsync(query);
        _factory.StubDocumentList = VisibleOnly();
        var withoutHidden = await PageAsync(query);

        withHidden.Should().BeEquivalentTo(withoutHidden, o => o.WithStrictOrdering());
        withHidden.Items.Should().NotContain(d => d.Attributes["confidentiality"] == "confidential");
    }

    // 🔴 他人の個人資料（区分では読める。管理面の一覧からは一律に除く）も、新しい絞り込み・件数のどれにも現れない。
    [Theory]
    [InlineData("?origin=collected")]
    [InlineData("?source=col%3Akabutan")]
    [InlineData("?project=ai-stock-trading")]
    [InlineData("?tag=News")]
    [InlineData("")]
    public async Task 他人の個人資料は新しい絞り込みの件数にもプロジェクト別の件数にも現れない(string query)
    {
        DocumentDto PersonalLike(int n, bool personal)
        {
            var attrs = Collected("kabutan");
            if (personal)
            {
                attrs["doc_scope"] = "private-note";
                attrs["owner"] = "someone-else";
            }
            return Doc(n, attributes: attrs, tags: ["News"]);
        }

        // 陽性対照: 個人資料の印を外した同じ 2 件は数えられる。
        _factory.StubDocumentList = [.. VisibleOnly(), PersonalLike(9, personal: false), PersonalLike(10, personal: false)];
        var control = await PageAsync("?source=col%3Akabutan");
        (control.Total, control.ProjectCounts["ai-stock-trading"]).Should().Be((4, 6));

        _factory.StubDocumentList = [.. VisibleOnly(), PersonalLike(9, personal: true), PersonalLike(10, personal: true)];
        var withPersonal = await PageAsync(query);
        _factory.StubDocumentList = VisibleOnly();
        var withoutPersonal = await PageAsync(query);

        withPersonal.Should().BeEquivalentTo(withoutPersonal, o => o.WithStrictOrdering());
        withPersonal.SourceCounts.Values.Sum().Should().Be(6);
    }

    // 許可ポリシーが無いときは、新しい件数も空（`{}`）。
    [Fact]
    public async Task 許可ポリシーが無いときデータソース別とプロジェクト別の件数は空()
    {
        _factory.StubDocumentList = Mixed();
        _factory.SearchScopeGranted = false;

        var page = await PageAsync("?origin=collected");

        (page.Total, page.UnfilteredTotal).Should().Be((0, 0));
        page.SourceCounts.Should().BeEmpty();
        page.ProjectCounts.Should().BeEmpty();
    }

    // ── 検証 ─────────────────────────────────────────────────

    [Theory]
    [InlineData("?origin=machine")]
    [InlineData("?origin=Collected")]
    [InlineData("?confidentiality=secret")]
    [InlineData("?createdFrom=yesterday")]
    [InlineData("?createdBefore=2026-13-01")]
    [InlineData("?createdFrom=2026-10-05T00:00:00Z&createdBefore=2026-10-05T00:00:00Z")]
    [InlineData("?createdFrom=2026-10-06T00:00:00Z&createdBefore=2026-10-05T00:00:00Z")]
    public async Task 未知の値と壊れた日時と逆向きの範囲は400で後段を呼ばない(string query)
    {
        _factory.StubDocumentList = Mixed();

        var resp = await _factory.CreateClient().GetAsync($"/bff/documents{query}", Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.ListDocumentsRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("source", DocumentListFilter.MaxSourceLength)]
    [InlineData("tag", DocumentListFilter.MaxValueLength)]
    [InlineData("project", DocumentListFilter.MaxValueLength)]
    public async Task 長すぎる値は400で上限ちょうどは通る(string name, int max)
    {
        _factory.StubDocumentList = Mixed();
        var client = _factory.CreateClient();

        (await client.GetAsync($"/bff/documents?{name}={new string('a', max)}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/bff/documents?{name}={new string('a', max + 1)}", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
