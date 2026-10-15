using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;
using DocPb = Knowledge.Contracts.Grpc.Document.V1;

namespace Platform.Bff.Tests;

// FR-06, UC-03, SC-05, NFR-16, [[IADR-0009]], [[IADR-0537]], [[IADR-0545]] (#1923 段 2):
// 文書管理の一覧（`GET /bff/documents`）の**並べ替え・表題の部分一致・状態の絞り込み・件数の要約**。
//
// 🔴 **絞り込みは ABAC の後に当て、件数（`total` / `unfilteredTotal` / `statusCounts`）はどれも ABAC で絞った後の数である。**
//   権限外の文書を混ぜた台帳と混ぜない台帳で、絞り込み・並べ替えを付けた応答が 1 ビットも変わらないことで示す（陰性）。
//   陽性対照として、混ぜた権限外の文書が絞り込みの条件に一致すること（＝落としているのは ABAC である）を先に確かめる。
public class BffDocumentListFilterTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDocumentListFilterTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        // 読めるのは internal だけ（confidential は権限外）。
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal"])];
        _factory.ScopeBranches = null;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
        _factory.DocumentReadServerPageSize = null;
        _factory.DocumentReadFailContinuation = false;
        while (_factory.ListDocumentsRequests.TryDequeue(out _)) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DocumentDto Doc(int n, string title, string status, string confidentiality = "internal") => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{n:D12}"),
        Title = title,
        Status = status,
        Attributes = new Dictionary<string, string> { ["confidentiality"] = confidentiality },
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch.AddMinutes(100 - n),
    };

    // 読める 4 件（1・3・5・6）と、権限外の 2 件（2・4）。権限外の 2 件は表題も状態も絞り込みの条件に一致する。
    private static List<DocumentDto> Mixed() =>
    [
        Doc(1, "Alpha 決算短信", "published"),
        Doc(2, "alpha 秘匿", "published", "confidential"),
        Doc(3, "beta 開示", "draft"),
        Doc(4, "ALPHA 秘匿その 2", "draft", "confidential"),
        Doc(5, "ALPHA メモ", "draft"),
        Doc(6, "gamma", "published"),
    ];

    private static List<DocumentDto> VisibleOnly() => Mixed().Where(d => d.Attributes["confidentiality"] == "internal").ToList();

    private async Task<DocumentListPageDto> PageAsync(string query)
    {
        var resp = await _factory.CreateClient().GetAsync($"/bff/documents{query}", Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync(Ct));
        return (await resp.Content.ReadFromJsonAsync<DocumentListPageDto>(Ct))!;
    }

    private static IEnumerable<int> Numbers(DocumentListPageDto page) => page.Items.Select(d => (int)(d.Id.ToByteArray()[15]));

    // ── 並べ替え（後段へ渡す） ─────────────────────────────────────

    [Theory]
    [InlineData("", DocPb.DocumentListSort.UpdatedDesc)]
    [InlineData("?sort=updated-desc", DocPb.DocumentListSort.UpdatedDesc)]
    [InlineData("?sort=updated-asc", DocPb.DocumentListSort.UpdatedAsc)]
    [InlineData("?sort=created-desc", DocPb.DocumentListSort.CreatedDesc)]
    [InlineData("?sort=created-asc", DocPb.DocumentListSort.CreatedAsc)]
    [InlineData("?sort=title-asc", DocPb.DocumentListSort.TitleAsc)]
    [InlineData("?sort=title-desc", DocPb.DocumentListSort.TitleDesc)]
    public async Task 並べ替えは後段の要求へ渡す(string query, DocPb.DocumentListSort expected)
    {
        _factory.StubDocumentList = Mixed();

        await PageAsync(query);

        _factory.ListDocumentsRequests.Should().ContainSingle().Which.Sort.Should().Be(expected);
    }

    // 後段のページを辿る間、続きの要求も同じ並べ替えで出す（印は並べ替えを持つ。違えば後段が INVALID_ARGUMENT）。
    [Fact]
    public async Task 後段のページを辿る間も同じ並べ替えで要求する()
    {
        _factory.StubDocumentList = Mixed();
        _factory.DocumentReadServerPageSize = 2;

        await PageAsync("?sort=title-desc");

        var requests = _factory.ListDocumentsRequests.ToList();
        requests.Should().HaveCount(3);
        requests.Should().OnlyContain(r => r.Sort == DocPb.DocumentListSort.TitleDesc);
    }

    // ── 表題の部分一致・状態の絞り込み・件数 ───────────────────────────

    // 大文字小文字を区別しない部分一致。窓と `total` は一致した文書だけ、`unfilteredTotal` は絞る前（ABAC 後）の数。
    [Fact]
    public async Task 表題の部分一致は大文字小文字を区別せず窓と総件数を絞る()
    {
        _factory.StubDocumentList = Mixed();

        var page = await PageAsync("?q=alpha");

        Numbers(page).Should().Equal(1, 5);
        page.Total.Should().Be(2);
        page.UnfilteredTotal.Should().Be(4);
    }

    // 前後の空白は除く。空白だけは無指定（全件）。
    [Theory]
    [InlineData("?q=%20%20", 4)]
    [InlineData("?q=%20beta%20", 1)]
    public async Task 表題の検索語は前後の空白を除く(string query, int total)
    {
        _factory.StubDocumentList = Mixed();

        (await PageAsync(query)).Total.Should().Be(total);
    }

    // 状態で絞る。`statusCounts` は状態別の数（利用者の絞り込みに依らず、合計は `unfilteredTotal`）。
    [Fact]
    public async Task 状態で絞り状態別の件数は絞り込みに依らない()
    {
        _factory.StubDocumentList = Mixed();

        var page = await PageAsync("?status=draft&q=alpha");

        Numbers(page).Should().Equal(5);
        page.Total.Should().Be(1);
        page.UnfilteredTotal.Should().Be(4);
        page.StatusCounts.Should().BeEquivalentTo(new Dictionary<string, int> { ["published"] = 2, ["draft"] = 2 });
    }

    // 絞り込みの窓も絞った後の順番で切る（2 ページ目）。
    [Fact]
    public async Task 絞り込みの窓は絞った後の順番で切る()
    {
        _factory.StubDocumentList = Mixed();

        var second = await PageAsync("?status=published&offset=1&limit=1");

        Numbers(second).Should().Equal(6);
        (second.Total, second.Offset, second.Limit).Should().Be((2, 1, 1));
    }

    // 🔴 存在秘匿（IADR-0009）: 絞り込み・並べ替えを付けても、権限外の文書の有無で応答が変わらない。
    [Theory]
    [InlineData("?q=alpha")]
    [InlineData("?q=秘匿")]
    [InlineData("?status=draft")]
    [InlineData("?status=published&q=alpha")]
    [InlineData("?sort=title-asc&limit=1&offset=1")]
    [InlineData("?q=ALPHA&limit=1&offset=1")]
    public async Task 権限外の文書は絞り込みの件数にも状態別の件数にも現れない(string query)
    {
        // 陽性対照: 区分を広げたスコープでは、権限外の 2 件も「alpha」に一致して数えられる（落としているのは ABAC である）。
        _factory.StubDocumentList = Mixed();
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal", "confidential"])];
        (await PageAsync("?q=alpha")).Total.Should().Be(4);
        _factory.ScopeFilters = [new AttributeFilter("confidentiality", ["internal"])];

        var withHidden = await PageAsync(query);
        _factory.StubDocumentList = VisibleOnly();
        var withoutHidden = await PageAsync(query);

        withHidden.Should().BeEquivalentTo(withoutHidden, o => o.WithStrictOrdering());
        withHidden.Items.Should().NotContain(d => d.Attributes["confidentiality"] == "confidential");
    }

    // ── 検証 ─────────────────────────────────────────────────

    [Theory]
    [InlineData("?sort=title")]
    [InlineData("?sort=UPDATED-DESC")]
    [InlineData("?status=deleted")]
    [InlineData("?status=Published")]
    public async Task 未知の並べ替えと状態は400(string query)
    {
        _factory.StubDocumentList = Mixed();

        var resp = await _factory.CreateClient().GetAsync($"/bff/documents{query}", Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.ListDocumentsRequests.Should().BeEmpty("誤った要求で後段の台帳を 1 周しない");
    }

    // 表題の検索語は 200 文字まで。201 文字は 400（黙って切り詰めない）。境界の 200 文字は通る。
    [Fact]
    public async Task 表題の検索語は200文字まで()
    {
        _factory.StubDocumentList = Mixed();

        (await PageAsync($"?q={new string('a', DocumentListFilter.MaxTitleQueryLength)}")).Total.Should().Be(0);
        var resp = await _factory.CreateClient()
            .GetAsync($"/bff/documents?q={new string('a', DocumentListFilter.MaxTitleQueryLength + 1)}", Ct);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
